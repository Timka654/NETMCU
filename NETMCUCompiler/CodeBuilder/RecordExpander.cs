using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    public class RecordExpander
    {
        public static Compilation Expand(Compilation compilation)
        {
            var syntaxTreesList = compilation.SyntaxTrees.ToList();
            List<SyntaxTree> newTrees = new List<SyntaxTree>();
            bool hasChanges = false;

            foreach (var tree in syntaxTreesList)
            {
                var model = compilation.GetSemanticModel(tree);
                var rewriter = new RecordSyntaxRewriter(model);
                var newRoot = rewriter.Visit(tree.GetRoot());

                if (newRoot != tree.GetRoot())
                {
                    hasChanges = true;
                    if (tree.FilePath.Contains("Program.cs"))
                    {
                        Console.WriteLine("=== REWRITTEN RECORDS ===");
                        Console.WriteLine(newRoot.ToFullString());
                        Console.WriteLine("============================");
                    }
                }
                newTrees.Add(tree.WithRootAndOptions(newRoot, tree.Options));
            }

            if (!hasChanges)
                return compilation;

            return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTrees);
        }
    }

    class RecordSyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;

        public RecordSyntaxRewriter(SemanticModel model)
        {
            _model = model;
        }

        public override SyntaxNode VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            if (node.ParameterList == null)
            {
                return base.VisitRecordDeclaration(node);
            }

            string className = node.Identifier.Text;
            var members = new List<MemberDeclarationSyntax>();

            var ctorParameters = new List<ParameterSyntax>();
            var ctorStatements = new List<StatementSyntax>();

            // Implement IEquatable? No need for now if we don't do == properly, 
            // but let's just generate standard class members
            foreach (var param in node.ParameterList.Parameters)
            {
                var propName = param.Identifier.Text;
                var propType = param.Type;
                
                var propDecl = SyntaxFactory.PropertyDeclaration(propType, propName)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    })));
                members.Add(propDecl);

                var paramName = char.ToLower(propName[0]) + propName.Substring(1);
                var ctorParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(propType);
                ctorParameters.Add(ctorParam);

                var assign = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(propName),
                        SyntaxFactory.IdentifierName(paramName)
                    )
                );
                ctorStatements.Add(assign);

                // Add "WithProp" method to support our WithExpression rewrite
                // public Point WithX(int x) { this.X = x; return this; }
                var withParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(propType);
                var withAssign = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName(propName)),
                        SyntaxFactory.IdentifierName(paramName)
                    )
                );
                var withMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(className), "With" + propName)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(withParam)))
                    .WithBody(SyntaxFactory.Block(
                        withAssign,
                        SyntaxFactory.ReturnStatement(SyntaxFactory.ThisExpression())
                    ));
                members.Add(withMethod);
            }

            var ctorDecl = SyntaxFactory.ConstructorDeclaration(node.Identifier)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(ctorParameters)))
                .WithBody(SyntaxFactory.Block(ctorStatements));
            members.Add(ctorDecl);

            // Add Clone method for `with` expressions
            // public Point _Clone() { return new Point(X, Y); }
            var cloneArgs = node.ParameterList.Parameters.Select(p => 
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text))).ToList();

            var cloneMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(className), "_Clone")
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(
                        SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(className))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(cloneArgs)))
                    )
                ));
            members.Add(cloneMethod);

            members.AddRange(node.Members.Select(m => (MemberDeclarationSyntax)Visit(m)));

            var classDecl = SyntaxFactory.ClassDeclaration(node.Identifier)
                .WithModifiers(node.Modifiers)
                .WithMembers(SyntaxFactory.List(members));

            return classDecl.WithTriviaFrom(node);
        }

        public override SyntaxNode VisitWithExpression(WithExpressionSyntax node)
        {
            // First visit children
            var visitedNode = (WithExpressionSyntax)base.VisitWithExpression(node);

            // Rewrite `p1 with { Y = 30, X = 10 }` -> `p1._Clone().WithY(30).WithX(10)`
            var baseExpr = visitedNode.Expression;
            var initExpr = visitedNode.Initializer; // InitializerExpressionSyntax (ObjectInitializerExpression)

            ExpressionSyntax currentExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    baseExpr,
                    SyntaxFactory.IdentifierName("_Clone")
                )
            );

            if (initExpr != null)
            {
                foreach (var assign in initExpr.Expressions.OfType<AssignmentExpressionSyntax>())
                {
                    if (assign.Left is IdentifierNameSyntax id)
                    {
                        string propName = id.Identifier.Text;
                        currentExpr = SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                currentExpr,
                                SyntaxFactory.IdentifierName("With" + propName)
                            )
                        ).WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(assign.Right)
                                )
                            )
                        );
                    }
                }
            }

            return currentExpr.WithTriviaFrom(node);
        }
    }
}