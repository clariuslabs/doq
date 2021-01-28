﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace NuDoq
{
    /// <summary>
    /// Reads .NET XML API documentation files, optionally augmenting 
    /// them with reflection information if reading from an assembly.
    /// </summary>
    public static class DocReader
    {
        /// <summary>
        /// Reads the specified documentation file and returns a lazily-constructed 
        /// set of members that can be visited.
        /// </summary>
        /// <param name="fileName">Path to the documentation file.</param>
        /// <returns>All documented members found in the given file.</returns>
        /// <exception cref="FileNotFoundException">Could not find documentation file to load.</exception>
        public static DocumentMembers Read(string fileName)
        {
            if (!File.Exists(fileName))
                throw new FileNotFoundException("Could not find documentation file to load.", fileName);

            var doc = XDocument.Load(fileName, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);

            return new DocumentMembers(doc, doc.Root.Element("members").Elements("member")
                .Where(element => element.Attribute("name") != null)
                //.OrderBy(element => element.Attribute("name").Value)
                .Select(element => CreateMember(element.Attribute("name").Value, element, ReadContent(element))));
        }

        /// <summary>
        /// Uses the specified assembly to locate a documentation file alongside the assembly by 
        /// changing the extension to ".xml". If the file is found, it will be read and all 
        /// found members will contain extended reflection information in the <see cref="Member.Info"/> 
        /// property.
        /// </summary>
        /// <param name="assembly">The assembly to read the documentation from.</param>
        /// <returns>All documented members found in the given file, together with the reflection metadata 
        /// association from the assembly.</returns>
        /// <exception cref="FileNotFoundException">Could not find documentation file to load.</exception>
        public static AssemblyMembers Read(Assembly assembly) => Read(assembly, null);

        /// <summary>
        /// Uses the specified assembly to locate a documentation file alongside the assembly by 
        /// changing the extension to ".xml". If the file is found, it will be read and all 
        /// found members will contain extended reflection information in the <see cref="Member.Info"/> 
        /// property.
        /// </summary>
        /// <param name="assembly">The assembly to read the documentation from.</param>
        /// <param name="documentationFilename">Path to the documentation file.</param>
        /// <returns>All documented members found in the given file, together with the reflection metadata 
        /// association from the assembly.</returns>
        /// <exception cref="FileNotFoundException">Could not find documentation file to load.</exception>
        public static AssemblyMembers Read(Assembly assembly, string? documentationFilename)
        {
            var fileName = documentationFilename;

            if (string.IsNullOrEmpty(fileName))
                fileName = Path.ChangeExtension(assembly.Location, ".xml");

            if (!File.Exists(fileName))
                throw new FileNotFoundException("Could not find documentation file to load. Expected: " + fileName, fileName);

            var doc = XDocument.Load(fileName, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);
            var map = new MemberIdMap();
            map.Add(assembly);

            return new AssemblyMembers(assembly, map, doc, doc.Root.Element("members").Elements("member")
                .Where(element => element.Attribute("name") != null)
                //.OrderBy(e => e.Attribute("name").Value)
                .Select(element => CreateMember(element.Attribute("name").Value, element, ReadContent(element)))
                .Select(member => ReplaceExtensionMethods(member, map))
                .Select(member => ReplaceTypes(member, map))
                .Select(member => SetInfo(member, map)));
        }

        /// <summary>
        /// Sets the extended reflection info if found in the map.
        /// </summary>
        static Member SetInfo(Member member, MemberIdMap map)
        {
            member.Info = map.FindMember(member.Id);

            return member;
        }

        /// <summary>
        /// Replaces the generic <see cref="TypeDeclaration"/> with 
        /// concrete types according to the reflection information.
        /// </summary>
        static Member ReplaceTypes(Member member, MemberIdMap map)
        {
            if (member.Kind != MemberKinds.Type)
                return member;

            var type = (Type)map.FindMember(member.Id);
            if (type == null)
                return member;

            if (type.IsInterface)
                return new Interface(member.Id, member.Elements);
            if (type.IsClass)
                return new Class(member.Id, member.Elements);
            if (type.IsEnum)
                return new Enum(member.Id, member.Elements);
            if (type.IsValueType)
                return new Struct(member.Id, member.Elements);

            return member;
        }

        /// <summary>
        /// Replaces the generic method element with a more specific extension method 
        /// element as needed.
        /// </summary>
        /// <param name="member">The member.</param>
        /// <param name="map">The map.</param>
        /// <returns></returns>
        static Member ReplaceExtensionMethods(Member member, MemberIdMap map)
        {
            if (member.Kind != MemberKinds.Method)
                return member;

            var method = (MethodBase)map.FindMember(member.Id);
            if (method == null)
                return member;

            if (method.GetCustomAttributes(true).Any(attr => attr.GetType().FullName == "System.Runtime.CompilerServices.ExtensionAttribute"))
            {
                var extendedTypeId = map.FindId(method.GetParameters()[0].ParameterType);
                if (!string.IsNullOrEmpty(extendedTypeId))
                    return new ExtensionMethod(member.Id, extendedTypeId, member.Elements);
            }

            return member;
        }

        /// <summary>
        /// Creates the appropriate type of member according to the member id prefix.
        /// </summary>
        static Member CreateMember(string memberId, XElement element, IEnumerable<Element> children)
        {
            Member? member = (memberId[0]) switch
            {
                'T' => new TypeDeclaration(memberId, children),
                'F' => new Field(memberId, children),
                'P' => new Property(memberId, children),
                'M' => new Method(memberId, children),
                'E' => new Event(memberId, children),
                _ => new UnknownMember(memberId),
            };
            member.SetLineInfo(element);
            return member;
        }

        /// <summary>
        /// Reads all supported documentation elements.
        /// </summary>
        static IEnumerable<Element> ReadContent(XElement xml)
        {
            foreach (var node in xml.Nodes())
            {
                var element = default(Element);
                switch (node.NodeType)
                {
                    case XmlNodeType.Element:
                        var elementNode = (XElement)node;
                        element = elementNode.Name.LocalName switch
                        {
                            "summary" => new Summary(ReadContent(elementNode)),
                            "remarks" => new Remarks(ReadContent(elementNode)),
                            "example" => new Example(ReadContent(elementNode)),
                            "para" => new Para(ReadContent(elementNode)),
                            "param" => new Param(FindAttribute(elementNode, "name"), ReadContent(elementNode)),
                            "paramref" => new ParamRef(FindAttribute(elementNode, "name")),
                            "typeparam" => new TypeParam(FindAttribute(elementNode, "name"), ReadContent(elementNode)),
                            "typeparamref" => new TypeParamRef(FindAttribute(elementNode, "name")),
                            "code" => new Code(TrimCode(elementNode.Value)),
                            "c" => new C(elementNode.Value),
                            "see" => new See(FindAttribute(elementNode, "cref"), FindAttribute(elementNode, "langword"), elementNode.Value, ReadContent(elementNode)),
                            "seealso" => new SeeAlso(FindAttribute(elementNode, "cref"), elementNode.Value, ReadContent(elementNode)),
                            "list" => new List(FindAttribute(elementNode, "type"), ReadContent(elementNode)),
                            "listheader" => new ListHeader(ReadContent(elementNode)),
                            "term" => new Term(ReadContent(elementNode)),
                            "description" => new Description(ReadContent(elementNode)),
                            "item" => new Item(ReadContent(elementNode)),
                            "exception" => new Exception(FindAttribute(elementNode, "cref"), ReadContent(elementNode)),
                            "value" => new Value(ReadContent(elementNode)),
                            "returns" => new Returns(ReadContent(elementNode)),
                            _ => new UnknownElement(elementNode, ReadContent(elementNode)),
                        };
                        break;
                    case XmlNodeType.Text:
                        element = new Text(TrimText(((XText)node).Value));
                        break;
                    default:
                        break;
                }

                if (element != null)
                {
                    element.SetLineInfo(xml);
                    yield return element;
                }
            }
        }

        /// <summary>
        /// Retrieves an attribute value if found, otherwise, returns a null string.
        /// </summary>
        static string FindAttribute(XElement elementNode, string attributeName)
            => elementNode.Attributes().Where(x => x.Name == attributeName).Select(x => x.Value).FirstOrDefault();

        /// <summary>
        /// Trims the text by removing new lines and trimming the indent.
        /// </summary>
        static string TrimText(string content) => TrimLines(content, StringSplitOptions.RemoveEmptyEntries, " ");

        /// <summary>
        /// Trims the code by removing extra indent.
        /// </summary>
        static string TrimCode(string content) => TrimLines(content, StringSplitOptions.None, Environment.NewLine);

        static string TrimLines(string content, StringSplitOptions splitOptions, string joinWith)
        {
            var lines = content.Split(new[] { Environment.NewLine, "\n" }, splitOptions).ToList();

            if (lines.Count == 0)
                return string.Empty;

            // Remove leading and trailing empty lines which are used for wrapping in the doc XML.
            if (lines[0].Trim().Length == 0)
                lines.RemoveAt(0);

            if (lines.Count == 0)
                return string.Empty;

            if (lines[lines.Count - 1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);

            if (lines.Count == 0)
                return string.Empty;

            // The indent of the first line of content determines the base 
            // indent for all the lines, which   we should remove since it's just 
            // a doc gen artifact.
            var indent = lines[0].TakeWhile(c => char.IsWhiteSpace(c)).Count();
            // Indent in generated XML doc files is greater than 4 always. 
            // This allows us to optimize the case where the author actually placed 
            // whitespace inline in between tags.
            if (indent <= 4 && !string.IsNullOrEmpty(lines[0]) && lines[0][0] != '\t')
                indent = 0;

            return string.Join(joinWith, lines
                .Select(line =>
                    {
                        if (string.IsNullOrEmpty(line))
                            return line;
                        else if (line.Length < indent)
                            return string.Empty;
                        else
                            return line.Substring(indent);
                    })
                .ToArray());
        }
    }
}