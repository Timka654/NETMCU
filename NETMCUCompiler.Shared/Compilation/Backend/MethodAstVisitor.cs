using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Security.Claims;

namespace NETMCUCompiler.Shared.Compilation.Backend
{
    public class MethodAstVisitor(MethodCompilationContext context) : CSharpSyntaxWalker
    {
        private Stack<(string breakLabel, string continueLabel)> _loopContexts = new();

        public void VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            context.Class.Global.Backend.GenerateVariableDeclaration(context, node);
        }

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            VisitVariableDeclaration(node.Declaration);
        }
        public override void VisitForStatement(ForStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateForStatement(context, node.Condition,
                generateInit: () => {
                    if (node.Declaration != null) VisitVariableDeclaration(node.Declaration);
                    foreach (var initializer in node.Initializers) Visit(initializer);
                },
                generateBody: () => Visit(node.Statement),
                generateIncrementor: () => {
                    foreach (var incrementor in node.Incrementors) Visit(incrementor);
                },
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateForEachStatement(context, node,
                generateBody: () => Visit(node.Statement),
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitTryStatement(TryStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateTryStatement(context,
                generateTryBlock: () => Visit(node.Block),
                generateCatchBlock: (catchClause) => Visit(catchClause.Block),
                generateFinallyBlock: node.Finally != null ? () => Visit(node.Finally.Block) : null,
                node.Catches, node.Finally);
        }

        public override void VisitThrowStatement(ThrowStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateThrowStatement(context, node.Expression);
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateWhileStatement(context, node.Condition, 
                generateBody: () => Visit(node.Statement),
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateDoStatement(context, node.Condition,
                generateBody: () => Visit(node.Statement),
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitReturnStatement(ReturnStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateReturnStatement(context, node.Expression);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateSwitchStatement(context, node.Expression, node.Sections,
                generateSectionBody: (section) =>
                {
                    foreach (var statement in section.Statements)
                        Visit(statement);
                },
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_loopContexts.Count == 0) throw new Exception("Оператор break вне цикла");
            context.Class.Global.Backend.GenerateBreakStatement(context, _loopContexts.Peek().breakLabel);
        }

        public override void VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_loopContexts.Count == 0) throw new Exception("Оператор continue вне цикла");
            context.Class.Global.Backend.GenerateContinueStatement(context, _loopContexts.Peek().continueLabel);
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            context.Class.Global.Backend.GeneratePrefixUnaryExpression(context, node);
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            context.Class.Global.Backend.GeneratePostfixUnaryExpression(context, node);
        }

        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            base.VisitExpressionStatement(node);
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            context.Class.Global.Backend.GenerateLiteralExpression(context, node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            context.Class.Global.Backend.GenerateIdentifierName(context, node);
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            context.Class.Global.Backend.GenerateAssignmentExpression(context, node);
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateIfStatement(context, node.Condition, 
                generateTrueBlock: () => Visit(node.Statement),
                generateFalseBlock: node.Else != null ? () => Visit(node.Else.Statement) : null);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            context.Class.Global.Backend.GenerateInvocationExpression(context, node);
        }

    }
}
