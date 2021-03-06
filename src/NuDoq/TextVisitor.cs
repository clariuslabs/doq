﻿using System;
using System.Text;

namespace NuDoq
{
    /// <summary>
    /// Visitor that is used to render ToString for elements.
    /// </summary>
    class TextVisitor : Visitor
    {
        readonly StringBuilder builder = new StringBuilder();

        public override void VisitC(C code)
        {
            base.VisitC(code);
            builder.Append(code.Content);
        }

        public override void VisitCode(Code code)
        {
            base.VisitCode(code);
            builder.AppendLine().Append(code.Content).AppendLine();
        }

        public override void VisitText(Text text)
        {
            base.VisitText(text);
            builder.Append(text.Content);
        }

        public override void VisitParamRef(ParamRef paramRef)
        {
            base.VisitParamRef(paramRef);
            if (!string.IsNullOrEmpty(paramRef.Name))
                builder.Append(paramRef.Name);
        }

        public override void VisitTypeParamRef(TypeParamRef typeParamRef)
        {
            base.VisitTypeParamRef(typeParamRef);
            if (!string.IsNullOrEmpty(typeParamRef.Name))
                builder.Append(typeParamRef.Name);
        }

        public override void VisitSee(See see)
        {
            base.VisitSee(see);
            if (!string.IsNullOrEmpty(see.Cref))
                builder.Append(see.Cref.Substring(2));
        }

        public override void VisitPara(Para para)
        {
            // Avoid double line breaks between adjacent <para> elements.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (builder.Length < 2 ||
                    new string(new char[] { builder[builder.Length - 2], builder[builder.Length - 1] }) != Environment.NewLine)
                    builder.AppendLine();
            }
            else if (builder.Length < 1 ||
                builder[builder.Length - 1].ToString() != Environment.NewLine)
            {
                builder.AppendLine();
            }

            base.VisitPara(para);
            builder.AppendLine();
        }

        public string Text => builder.ToString();
    }
}