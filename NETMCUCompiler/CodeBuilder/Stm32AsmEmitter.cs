using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;

namespace NETMCUCompiler.CodeBuilder
{
    public class Stm32MethodBuilder : CSharpSyntaxWalker
    {
        CompilationContext context = new();

        public (string code, byte[] logic) BuildAsm(MethodDeclarationSyntax tree)
        {
            var classNode = tree.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classNode != null)
            {
                foreach (var member in classNode.Members)
                {
                    if (member is FieldDeclarationSyntax field && field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            if (variable.Initializer?.Value is LiteralExpressionSyntax literal)
                            {
                                int val = ASMInstructions.ParseLiteral(literal);
                                string name = variable.Identifier.Text;
                                context.ConstantMap[name] = val; // Просто d2
                                context.ConstantMap[$"{classNode.Identifier.Text}.{name}"] = val; // Program.d2
                            }
                        }
                    }
                }
            }

            // Сканируем константы внутри метода (например, d2)
            foreach (var localConst in tree.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                        .Where(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))))
            {
                foreach (var v in localConst.Declaration.Variables)
                {
                    if (v.Initializer?.Value is LiteralExpressionSyntax lit)
                        context.ConstantMap[v.Identifier.Text] = ASMInstructions.ParseLiteral(lit);
                }
            }
            ASMInstructions.EmitFunctionFrame(tree, context, () => Visit(tree));

            return (context.Asm.ToString(), context.Bin.ToArray());
        }
        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                string varName = variable.Identifier.Text;
                int currentReg;
                // Если переменная уже была объявлена (например, в другом месте или мы перезаписываем)
                if (context.RegisterMap.TryGetValue(varName, out int existingReg))
                {
                    currentReg = existingReg;
                }
                else
                {
                    // Выделяем новый регистр
                    if (context.NextFreeRegister > 11) throw new Exception("Закончились регистры r4-r11");
                    currentReg = context.NextFreeRegister++;
                    context.RegisterMap[varName] = currentReg;
                }

                context.Emit($"@ Allocation: {varName} -> r{currentReg}");

                // Если есть значение (например, = 10 + a)
                if (variable.Initializer != null)
                {
                    ASMInstructions.EmitExpression(
                        variable.Initializer.Value,
                        currentReg,
                        context,
                        0
                    );
                }
            }
        }
        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            // Генерируем уникальные метки для начала и конца цикла
            string startLabel = context.NextLabel("WHILE_START");
            string endLabel = context.NextLabel("WHILE_END");

            // Метка начала: сюда будем прыгать для каждой итерации
            context.Emit($"{startLabel}:");

            // 1. Вычисляем условие. 
            // Если оно ложно (false) — прыгаем сразу на выход (endLabel)
            // Если истинно — просто идем дальше в тело цикла
            ASMInstructions.EmitLogicalCondition(node.Condition, "", endLabel, context);

            // 2. Тело цикла: рекурсивно посещаем все стейтменты внутри { }
            Visit(node.Statement);

            // 3. Прыжок обратно на проверку условия
            context.Emit($"B {startLabel}");

            // Метка выхода
            context.Emit($"{endLabel}:");
        }
        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            if (node.Expression is AssignmentExpressionSyntax assignment)
            {
                int targetReg = context.GetVarRegister(assignment.Left.ToString());

                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    // Обычное a = 1;
                    ASMInstructions.EmitExpression(assignment.Right, targetReg, context);
                }
                else
                {
                    // Составное присваивание: +=, -=, *=, /=
                    // 1. Определяем, какая мат. операция скрыта за знаком
                    SyntaxKind opKind = assignment.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
                        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
                        _ => SyntaxKind.None
                    };

                    if (opKind != SyntaxKind.None)
                    {
                        // 2. Вычисляем правую часть во временный регистр r0
                        ASMInstructions.EmitExpression(assignment.Right, 0, context);

                        // 3. Выполняем операцию: target = target (op) r0
                        // Например: r4 = r4 + r0
                        ASMInstructions.EmitArithmeticOp(opKind, targetReg, targetReg, 0, context);
                    }
                }
            }
            // Про Test() и IF мы поговорим в следующих шагах (Шаг 4 и 5)
            base.VisitExpressionStatement(node);
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            string trueLabel = $"L_TRUE_{context.LabelCount++}";
            string falseLabel = $"L_FALSE_{context.LabelCount++}";
            string endLabel = $"L_END_{context.LabelCount++}";

            // Разбираем цепочку условий
            ASMInstructions.EmitLogicalCondition(node.Condition, trueLabel, falseLabel, context);

            // Тело TRUE
            context.Asm.AppendLine($"{trueLabel}:");
            Visit(node.Statement);
            ASMInstructions.EmitJump(endLabel, context);

            // Тело FALSE (или следующий Else If)
            context.Asm.AppendLine($"{falseLabel}:");
            if (node.Else != null)
            {
                Visit(node.Else.Statement);
            }

            context.Asm.AppendLine($"{endLabel}:");
        }

        private void ProcessCondition(ExpressionSyntax condition, string failedLabel)
        {
            if (condition is BinaryExpressionSyntax bin)
            {
                // Если это логическое ИЛИ (||)
                if (bin.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    string trueLabel = $"L_CONDITION_TRUE_{context.LabelCount++}";
                    // Если левая часть верна -> прыгаем в тело (trueLabel)
                    // Здесь нужна инверсия инверсии, но для простоты:
                    ProcessSubCondition(bin.Left, trueLabel, isOr: true);
                    // Если нет -> проверяем правую часть
                    ProcessSubCondition(bin.Right, failedLabel, isOr: false);
                    context.Asm.AppendLine($"{trueLabel}:");
                }
                else
                {
                    // Обычное одиночное сравнение
                    ProcessSubCondition(condition, failedLabel, isOr: false);
                }
            }
        }

        private void ProcessSubCondition(ExpressionSyntax expr, string label, bool isOr)
        {
            if (expr is BinaryExpressionSyntax bin)
            {
                // Извлекаем чистую переменную и значение
                int leftReg = context.GetVarRegister(bin.Left.ToString().Trim());
                int rightVal = int.Parse(bin.Right.ToString().Trim());

                ASMInstructions.EmitCompareImmediate(leftReg, rightVal, context);

                if (isOr)
                {
                    // Для OR: если условие ВЕРНО -> прыгаем в тело
                    string jmp = bin.Kind() switch
                    {
                        SyntaxKind.EqualsExpression => "BEQ",
                        SyntaxKind.GreaterThanExpression => "BGT",
                        _ => "BEQ"
                    };
                    context.Asm.AppendLine($"    {jmp} {label}");
                    context.Bytecode(0x00); context.Bytecode(0xD0);
                }
                else
                {
                    // Для обычного IF/AND: если НЕВЕРНО -> прыгаем в ELSE
                    ASMInstructions.EmitConditionalBranch(bin.Kind(), label, context);
                }
            }
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var args = node.ArgumentList.Arguments;

            // 1. Загружаем аргументы в r0-r3 (стандарт ARM)
            for (int i = 0; i < args.Count && i < 4; i++)
            {
                // Вычисляем значение аргумента прямо в регистр i
                ASMInstructions.EmitExpression(args[i].Expression, i, context,
                        0);
            }

            // 2. Генерируем BL
            string methodName = node.Expression.ToString();
            ASMInstructions.EmitCall(methodName, context);

            base.VisitInvocationExpression(node);
        }

    }
}
