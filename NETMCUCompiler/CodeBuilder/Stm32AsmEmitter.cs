using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Security.Claims;

namespace NETMCUCompiler.CodeBuilder
{
    public class Stm32MethodBuilder(MethodCompilationContext context) : CSharpSyntaxWalker
    {
        private Stack<(string breakLabel, string continueLabel)> _loopContexts = new();

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
        public void VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            foreach (var variable in node.Variables)
            {
                string varName = variable.Identifier.Text;
                int currentReg;
                if (context.RegisterMap.TryGetValue(varName, out int existingReg))
                {
                    currentReg = existingReg;
                }
                else
                {
                    if (context.NextFreeRegister > 11) throw new Exception("Закончились регистры r4-r11");
                    currentReg = context.NextFreeRegister++;
                    context.RegisterMap[varName] = currentReg;
                }

                context.Emit($"@ Allocation: {varName} -> r{currentReg}");

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

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            VisitVariableDeclaration(node.Declaration);
        }
        public override void VisitForStatement(ForStatementSyntax node)
        {
            // 1. Инициализация (может быть объявление переменной или просто присваивание)
            if (node.Declaration != null)
            {
                VisitVariableDeclaration(node.Declaration);
            }
            foreach (var initializer in node.Initializers)
            {
                Visit(initializer);
            }

            string startLabel = context.NextLabel("FOR_START");
            string endLabel = context.NextLabel("FOR_END");
            string incLabel = context.NextLabel("FOR_INC"); // Для continue

            _loopContexts.Push((endLabel, incLabel));

            // Метка начала
            context.Asm.AppendLine($"{startLabel}:");

            // 2. Условие
            if (node.Condition != null)
            {
                ASMInstructions.EmitLogicalCondition(node.Condition, "", endLabel, context);
            }

            // 3. Тело
            Visit(node.Statement);

            // Метка инкремента (сюда будет прыгать continue, если мы его реализуем)
            context.Asm.AppendLine($"{incLabel}:");

            // 4. Инкремент
            foreach (var incrementor in node.Incrementors)
            {
                Visit(incrementor);
            }

            // 5. Прыжок на начало
            ASMInstructions.EmitJump(startLabel, context);

            // Метка выхода
            context.Asm.AppendLine($"{endLabel}:");

            _loopContexts.Pop();
        }

        public override void VisitTryStatement(TryStatementSyntax node)
        {
            context.Emit("@ TRY BLOCK START");

            string catchLabel = context.NextLabel("CATCH_HANDLER");
            string endLabel = context.NextLabel("TRY_END");

            // Помещаем адрес обработчика в стек
            context.Emit($"ldr r0, ={catchLabel}");
            context.Emit("bl NETMCU_TryPush");

            Visit(node.Block);

            // Если блок выполнился успешно без throw, снимаем обработчик
            context.Emit("bl NETMCU_TryPop");
            context.Emit($"b {endLabel}");

            // Сам функция-обработчик catch (вызывается из throw, не раскручивая стек локальных переменных)
            context.Asm.AppendLine($"{catchLabel}:");
            context.Emit("push {lr}");

            // Снимаем себя из стека обработчиков, чтобы следующий throw полетел выше
            context.Emit("bl NETMCU_TryPop"); 

            // Обрабатываем блоки catch
            foreach (var catchClause in node.Catches)
            {
                context.Emit($"@ CATCH BLOCK: {catchClause.Declaration?.Type?.ToString()}");
                Visit(catchClause.Block);
            }

            // Возврат в throw! (как и просил пользователь, без сложной раскрутки)
            context.Emit("pop {pc}");

            context.Asm.AppendLine($"{endLabel}:");

            if (node.Finally != null)
            {
                context.Emit("@ FINALLY BLOCK");
                Visit(node.Finally.Block);
            }

            context.Emit("@ TRY BLOCK END");
        }

        public override void VisitThrowStatement(ThrowStatementSyntax node)
        {
            context.Emit("@ THROW EXECUTION");
            if (node.Expression != null)
            {
                // Помещаем результат выражения сразу в r0 (первый аргумент для вызова)
                ASMInstructions.EmitExpression(node.Expression, 0, context);
            }
            else
            {
                context.Emit("mov r0, #0 @ Rethrow or null exception");
            }

            context.Emit("bl NETMCU_Throw");
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            // Генерируем уникальные метки для начала и конца цикла
            string startLabel = context.NextLabel("WHILE_START");
            string endLabel = context.NextLabel("WHILE_END");

            _loopContexts.Push((endLabel, startLabel));

            // Метка начала: сюда будем прыгать для каждой итерации
            context.Asm.AppendLine($"{startLabel}:");

            // 1. Вычисляем условие. 
            // Если оно ложно (false) — прыгаем сразу на выход (endLabel)
            // Если истинно — просто идем дальше в тело цикла
            ASMInstructions.EmitLogicalCondition(node.Condition, "", endLabel, context);

            // 2. Тело цикла: рекурсивно посещаем все стейтменты внутри { }
            Visit(node.Statement);

            // 3. Прыжок обратно на проверку условия
            ASMInstructions.EmitJump(startLabel, context);

            // Метка выхода
            context.Asm.AppendLine($"{endLabel}:");

            _loopContexts.Pop();
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            string startLabel = context.NextLabel("DO_START");
            string endLabel = context.NextLabel("DO_END");
            string condLabel = context.NextLabel("DO_COND"); // Для continue

            _loopContexts.Push((endLabel, condLabel));

            context.Asm.AppendLine($"{startLabel}:");

            Visit(node.Statement);

            context.Asm.AppendLine($"{condLabel}:");

            // Проверяем условие. Если true -> прыгаем в начало (startLabel).
            ASMInstructions.EmitLogicalCondition(node.Condition, startLabel, endLabel, context);

            context.Asm.AppendLine($"{endLabel}:");

            _loopContexts.Pop();
        }

        public override void VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression != null)
            {
                // Результат возврата должен лечь в R0
                ASMInstructions.EmitExpression(node.Expression, 0, context);
            }

            // Прыгаем в эпилог текущего метода (метода, который будет генерировать POP {pc})
            string methodName = context.Name;
            ASMInstructions.EmitJump($"{methodName}_exit", context);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            int switchReg = context.NextFreeRegister++;
            ASMInstructions.EmitExpression(node.Expression, switchReg, context);

            string endSwitchLabel = context.NextLabel("SWITCH_END");

            // В C# break внутри switch должен выйти из switch.
            _loopContexts.Push((endSwitchLabel, "ERROR_CONTINUE_IN_SWITCH"));

            int tmpReg = context.NextFreeRegister++;
            string defaultLabel = null;

            var sectionLabels = new Dictionary<SwitchSectionSyntax, string>();

            // 1. Создаем метки для блоков и генерируем проверки кейсов
            foreach (var section in node.Sections)
            {
                string sectionLabel = context.NextLabel("SWITCH_SECTION");
                sectionLabels[section] = sectionLabel;

                foreach (var label in section.Labels)
                {
                    if (label is DefaultSwitchLabelSyntax)
                    {
                        defaultLabel = sectionLabel;
                    }
                    else if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        // Сравниваем
                        ASMInstructions.EmitExpression(caseLabel.Value, tmpReg, context);
                        ASMInstructions.EmitCompare(switchReg, tmpReg, context);
                        ASMInstructions.EmitBranch(sectionLabel, "EQ", context);
                    }
                }
            }

            // 2. Если ни один не подошел, и есть default:
            if (defaultLabel != null)
            {
                ASMInstructions.EmitJump(defaultLabel, context);
            }
            else
            {
                // Если нет default, прыгаем в конец
                ASMInstructions.EmitJump(endSwitchLabel, context);
            }

            // 3. Вывод тел кейсов
            foreach (var section in node.Sections)
            {
                context.Asm.AppendLine($"{sectionLabels[section]}:");
                foreach (var statement in section.Statements)
                {
                    Visit(statement);
                }
            }

            // Конец
            context.Asm.AppendLine($"{endSwitchLabel}:");
            _loopContexts.Pop();

            // Освобождаем регистры
            context.NextFreeRegister -= 2; 
        }

        public override void VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_loopContexts.Count == 0) throw new Exception("Оператор break вне цикла");
            ASMInstructions.EmitJump(_loopContexts.Peek().breakLabel, context);
        }

        public override void VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_loopContexts.Count == 0) throw new Exception("Оператор continue вне цикла");
            ASMInstructions.EmitJump(_loopContexts.Peek().continueLabel, context);
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            HandleIncrementDecrement(node.Operand, node.Kind());
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            HandleIncrementDecrement(node.Operand, node.Kind());
        }

        private void HandleIncrementDecrement(ExpressionSyntax operand, SyntaxKind kind)
        {
            if (operand is IdentifierNameSyntax id && context.RegisterMap.TryGetValue(id.Identifier.Text, out int reg))
            {
                if (kind == SyntaxKind.PreIncrementExpression || kind == SyntaxKind.PostIncrementExpression)
                {
                    // reg = reg + 1
                    ASMInstructions.EmitOpWithImmediate(SyntaxKind.AddExpression, reg, reg, 1, context);
                }
                else if (kind == SyntaxKind.PreDecrementExpression || kind == SyntaxKind.PostDecrementExpression)
                {
                    // reg = reg - 1
                    ASMInstructions.EmitOpWithImmediate(SyntaxKind.SubtractExpression, reg, reg, 1, context);
                }
            }
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
            int val = ASMInstructions.ParseLiteral(node, context);
            int reg = context.NextFreeRegister++; // Или твоя логика выделения
            context.Emit($"MOVS R{reg}, #{val}");
            context.LastUsedRegister = reg;
        }
        public void EmitLoadStringAddress(string register, string symbolName)
        {
            // Предполагаем, что регистр всегда r0 для простоты
            //if (register != "r0")
            //{
            //    throw new NotSupportedException("Загрузка адресов строк поддерживается только для регистра r0.");
            //}

            // Добавляем запись о релокации данных.
            // Линкер позже запишет сюда абсолютный адрес символа.
            context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

            // Генерируем ассемблерный плейсхолдер и резервируем 8 байт
            // для пары инструкций MOVW/MOVT.
            context.Emit($"LDR {register}, ={symbolName} ; (placeholder for MOVW/MOVT)");
            context.Bin.Write(new byte[8], 0, 8);
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
            if (node.Left is ElementAccessExpressionSyntax elementAccess)
            {
                int destAddrReg = context.NextFreeRegister++;
                ASMInstructions.EmitExpression(elementAccess.Expression, destAddrReg, context);

                int indexReg = context.NextFreeRegister++;
                ASMInstructions.EmitExpression(elementAccess.ArgumentList.Arguments[0].Expression, indexReg, context);

                int sizeReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovImmediate(sizeReg, 4, context);
                ASMInstructions.EmitArithmeticOp(SyntaxKind.MultiplyExpression, indexReg, indexReg, sizeReg, context);

                ASMInstructions.EmitArithmeticOp(SyntaxKind.AddExpression, destAddrReg, destAddrReg, indexReg, context);

                int valueReg = 0;
                ASMInstructions.EmitExpression(node.Right, valueReg, context);

                if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    context.Emit($"STR r{valueReg}, [r{destAddrReg}, #0]");
                }
                else
                {
                    int tmpRead = context.NextFreeRegister++;
                    context.Emit($"LDR r{tmpRead}, [r{destAddrReg}, #0]");

                    SyntaxKind opKind = node.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                        _ => SyntaxKind.None
                    };

                    if (opKind != SyntaxKind.None)
                    {
                        ASMInstructions.EmitArithmeticOp(opKind, tmpRead, tmpRead, valueReg, context);
                        context.Emit($"STR r{tmpRead}, [r{destAddrReg}, #0]");
                    }
                    context.NextFreeRegister--;
                }

                context.NextFreeRegister -= 3;
                return;
            }

            int standardValueReg = 0;

            ASMInstructions.EmitExpression(node.Right, standardValueReg, context);

            if (node.Left is MemberAccessExpressionSyntax memberAccess)
            {
                HandleStructAssignment(node, memberAccess, standardValueReg);
            }
            else
            {
                string varName = node.Left.ToString();
                if (context.RegisterMap.TryGetValue(varName, out int destReg))
                {
                    HandleLocalAssignment(node, destReg, standardValueReg);
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
        //public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        //{
        //    if (context.ExceptTypes.Contains(node)) return;
        //    base.VisitClassDeclaration(node);
        //}

        //private void ProcessCondition(ExpressionSyntax condition, string failedLabel)
        //{
        //    if (condition is BinaryExpressionSyntax bin)
        //    {
        //        // Если это логическое ИЛИ (||)
        //        if (bin.IsKind(SyntaxKind.LogicalOrExpression))
        //        {
        //            string trueLabel = $"L_CONDITION_TRUE_{context.LabelCount++}";
        //            // Если левая часть верна -> прыгаем в тело (trueLabel)
        //            // Здесь нужна инверсия инверсии, но для простоты:
        //            ProcessSubCondition(bin.Left, trueLabel, isOr: true);
        //            // Если нет -> проверяем правую часть
        //            ProcessSubCondition(bin.Right, failedLabel, isOr: false);
        //            context.Asm.AppendLine($"{trueLabel}:");
        //        }
        //        else
        //        {
        //            // Обычное одиночное сравнение
        //            ProcessSubCondition(condition, failedLabel, isOr: false);
        //        }
        //    }
        //}

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
            // Метод может не возвращать значение, или мы игнорируем его
            // Обработчик в EmitExpression уже знает как сгенерировать BL и подготовить аргументы
            ASMInstructions.EmitExpression(node, 0, context);
        }

    }
}
