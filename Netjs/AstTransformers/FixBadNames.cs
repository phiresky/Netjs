using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.NRefactory.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Netjs.AstTransformers
{
    class FixBadNames : DepthFirstAstVisitor, IAstTransform
    {
        public void Run(AstNode compilationUnit)
        {
            compilationUnit.AcceptVisitor(this);
        }

        public override void VisitIdentifier(Identifier identifier)
        {
            base.VisitIdentifier(identifier);
            var o = identifier.Name;
            var n = o.Replace('<', '_').Replace('>', '_');
            if (n != o)
            {
                identifier.Name = n;
            }
        }
    }
}
