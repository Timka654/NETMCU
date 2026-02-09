using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Security.Claims;

namespace NETMCUCompiler.CodeBuilder
{
    public class Stm32MethodBuilder(CompilationContext context) : CSharpSyntaxWalker
    {
        //public void BuildAsm(MethodDeclarationSyntax tree)
        //{
        //    var classNode = tree.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        //    if (classNode != null)
        //    {
        //        foreach (var member in classNode.Members)
        //        {
        //            if (member is FieldDeclarationSyntax field && field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
        //            {
        //                foreach (var variable in field.Declaration.Variables)
        //                {
        //                    if (variable.Initializer?.Value is LiteralExpressionSyntax literal)
        //                    {
        //                        int val = ASMInstructions.ParseLiteral(literal);
        //                        string name = variable.Identifier.Text;
        //                        context.ConstantMap[name] = val; // Просто d2
        //                        context.ConstantMap[$"{classNode.Identifier.Text}.{name}"] = val; // Program.d2
        //                    }
        //                }
        //            }
        //        }
        //    }

        //    // Сканируем константы внутри метода (например, d2)
        //    foreach (var localConst in tree.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
        //                .Where(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))))
        //    {
        //        foreach (var v in localConst.Declaration.Variables)
        //        {
        //            if (v.Initializer?.Value is LiteralExpressionSyntax lit)
        //                context.ConstantMap[v.Identifier.Text] = ASMInstructions.ParseLiteral(lit);
        //        }
        //    }

        //    ASMInstructions.EmitFunctionFrame(tree, context, () => Visit(tree));
        //}
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
            //if (node.Expression is AssignmentExpressionSyntax assignment)
            //{
            //    int targetReg = context.GetVarRegister(assignment.Left.ToString());

            //    if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            //    {
            //        // Обычное a = 1;
            //        ASMInstructions.EmitExpression(assignment.Right, targetReg, context);
            //    }
            //    else
            //    {
            //        // Составное присваивание: +=, -=, *=, /=
            //        // 1. Определяем, какая мат. операция скрыта за знаком
            //        SyntaxKind opKind = assignment.Kind() switch
            //        {
            //            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
            //            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
            //            SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
            //            SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
            //            _ => SyntaxKind.None
            //        };

            //        if (opKind != SyntaxKind.None)
            //        {
            //            // 2. Вычисляем правую часть во временный регистр r0
            //            ASMInstructions.EmitExpression(assignment.Right, 0, context);

            //            // 3. Выполняем операцию: target = target (op) r0
            //            // Например: r4 = r4 + r0
            //            ASMInstructions.EmitArithmeticOp(opKind, targetReg, targetReg, 0, context);
            //        }
            //    }
            //}
            // Про Test() и IF мы поговорим в следующих шагах (Шаг 4 и 5)
            base.VisitExpressionStatement(node);
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            int val = ASMInstructions.ParseLiteral(node);
            int reg = context.NextFreeRegister++; // Или твоя логика выделения
            context.Emit($"MOVS R{reg}, #{val}");
            context.LastUsedRegister = reg;
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            string name = node.Identifier.Text;
            if (context.RegisterMap.TryGetValue(name, out int reg))
            {
                context.LastUsedRegister = reg;
            }
            // Если переменная на стеке, нам нужно загрузить её в регистр для вычислений
            else if (context.StackMap.TryGetValue(name, out var stackVar))
            {
                int r = context.NextFreeRegister++;
                context.Emit($"LDR R{r}, [SP, #{stackVar.StackOffset}]");
                context.LastUsedRegister = r;
            }
        }

        private void HandleStructAssignment(AssignmentExpressionSyntax node, MemberAccessExpressionSyntax memberAccess, int srcReg)
        {
            string structName = memberAccess.Expression.ToString();
            string fieldName = memberAccess.Name.ToString();

            if (context.StackMap.TryGetValue(structName, out var stackVar))
            {
                if (stackVar.Metadata.FieldOffsets.TryGetValue(fieldName, out int fieldOffset))
                {
                    int totalOffset = stackVar.StackOffset + fieldOffset;

                    if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        context.Emit($"STR R{srcReg}, [SP, #{totalOffset}] @ {structName}.{fieldName} = val");
                    }
                    else
                    {
                        // Составное: config.Pin |= rSrc;
                        int tempReg = 0; // Используем r0 как временный для вычислений
                        context.Emit($"LDR R{tempReg}, [SP, #{totalOffset}] @ Load {structName}.{fieldName}");

                        SyntaxKind opKind = node.Kind() switch
                        {
                            SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,   // Исправлено
                            SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression, // Исправлено
                            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                            _ => SyntaxKind.None
                        };

                        if (opKind != SyntaxKind.None)
                        {
                            ASMInstructions.EmitArithmeticOp(opKind, tempReg, tempReg, srcReg, context);
                            context.Emit($"STR R{tempReg}, [SP, #{totalOffset}] @ Store updated {structName}.{fieldName}");
                        }
                    }
                }
            }
            else
            {
                throw new Exception($"Структура {structName} не найдена на стеке");
            }
        }
        private void HandleLocalAssignment(AssignmentExpressionSyntax node, int destReg, int srcReg)
        {
            if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                // Обычное x = y;
                if (destReg != srcReg)
                    context.Emit($"MOV R{destReg}, R{srcReg}");
            }
            else
            {
                // Составное присваивание: x |= y, x += y и т.д.
                // Используем корректные SyntaxKind из Roslyn
                SyntaxKind opKind = node.Kind() switch
                {
                    SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                    SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                    SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression, // Исправлено
                    SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,   // Исправлено
                    _ => SyntaxKind.None
                };

                if (opKind != SyntaxKind.None)
                {
                    // Выполняем операцию: Rdest = Rdest op Rsrc
                    ASMInstructions.EmitArithmeticOp(opKind, destReg, destReg, srcReg, context);
                }
            }
        }
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            // 1. Не берем LastUsedRegister, а определяем, КУДА мы хотим получить результат.
            // Для правой части идеально подходит R0 (стандарт ARM для возврата и параметров).
            int valueReg = 0;

            // 2. Генерируем код для вычисления правой части прямо в R0.
            // Теперь (1 << pin) превратится в цепочку команд, финал которой ляжет в R0.
            ASMInstructions.EmitExpression(node.Right, valueReg, context);

            // 3. Теперь записываем результат из R0 по назначению
            if (node.Left is MemberAccessExpressionSyntax memberAccess)
            {
                // Передаем R0 как источник данных для STR
                HandleStructAssignment(node, memberAccess, valueReg);
            }
            else
            {
                string varName = node.Left.ToString();
                if (context.RegisterMap.TryGetValue(varName, out int destReg))
                {
                    // Если пишем в локальную переменную (регистр), просто MOV Rdest, R0
                    HandleLocalAssignment(node, destReg, valueReg);
                }
                else
                {
                    throw new Exception($"Переменная {varName} не объявлена");
                }
            }
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
        //public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        //{
        //    var typeName = node.Type.ToString();
        //    var meta = _typeManager.GetType(typeName);

        //    // 1. Подготовка размера для Alloc (в r0)
        //    ASMInstructions.EmitMovImmediate(0, meta.TotalSize, context);

        //    // 2. Вызов нативного аллокатора
        //    ASMInstructions.EmitCall("NETMCU__Memory__Alloc", context);

        //    // 3. Результат (указатель) теперь в r0. Переносим в целевой регистр переменной.
        //    int targetReg = context.NextFreeRegister++;
        //    ASMInstructions.EmitMovRegister(targetReg, 0, context);
        //}

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (context.ExceptTypes.Contains(node)) return;
            base.VisitClassDeclaration(node);
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
            // 1. Получаем символ метода через семантическую модель (уже есть в твоем коде)
            var methodSymbol = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (methodSymbol == null) throw new Exception($"Символ не найден для: {node}");

            var args = node.ArgumentList.Arguments;
            int regOffset = 0;

            // 2. Логика 'this' (указатель на объект)
            if (!methodSymbol.IsStatic)
            {
                // Если это вызов типа myObj.Set(), вычисляем myObj в r0
                if (node.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    ASMInstructions.EmitExpression(memberAccess.Expression, 0, context);
                }
                else
                {
                    // Если просто Set(), значит работаем с текущим объектом (r4)
                    context.Emit("MOV r0, r4");
                }
                regOffset = 1; // Все остальные аргументы сдвигаются на r1, r2, r3
            }

            // 3. Загружаем аргументы (r0-r3 согласно AAPCS)
            for (int i = 0; i < args.Count; i++)
            {
                var argument = node.ArgumentList.Arguments[i];
                if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                {
                    string varName = argument.Expression.ToString();
                    if (context.StackMap.TryGetValue(varName, out var stackVar))
                    {
                        // Нам нужен АДРЕС структуры. В ARM это: ADD Rd, SP, #offset
                        int argReg = i; // Обычно R0-R3
                        context.Emit($"ADD R{argReg}, SP, #{stackVar.StackOffset}");
                    }
                    continue;
                }

                int targetReg = i + regOffset;
                if (targetReg > 3)
                    throw new Exception("Поддерживается максимум 4 аргумента (включая this)");

                ASMInstructions.EmitExpression(args[i].Expression, targetReg, context);
            }

            // 4. Проверка на NativeCall
            var nativeAttr = methodSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass.Name.Contains("NativeCall"));

            // Если есть атрибут, берем имя функции в Си из него, иначе используем FullName метода
            string nativeFunctionName = nativeAttr?.ConstructorArguments.FirstOrDefault().Value?.ToString();
            string callTarget = nativeFunctionName ?? methodSymbol.ToDisplayString();

            // 5. Генерируем BL
            ASMInstructions.EmitCall(callTarget, context, methodSymbol.IsStatic);

            // Если метод что-то возвращает, результат в r0. 
            // Нам нужно пометить, что r0 теперь занят результатом (для будущих присваиваний)
        }

    }
}
