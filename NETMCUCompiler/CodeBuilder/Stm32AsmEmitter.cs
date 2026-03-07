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
            context.MarkLabel(startLabel);

            // 2. Условие
            if (node.Condition != null)
            {
                ASMInstructions.EmitLogicalCondition(node.Condition, "", endLabel, context);
            }

            // 3. Тело
            Visit(node.Statement);

            // Метка инкремента (сюда будет прыгать continue, если мы его реализуем)
            context.MarkLabel(incLabel);

            // 4. Инкремент
            foreach (var incrementor in node.Incrementors)
            {
                Visit(incrementor);
            }

            // 5. Прыжок на начало
            ASMInstructions.EmitJump(startLabel, context);

            // Метка выхода
            context.MarkLabel(endLabel);

            _loopContexts.Pop();
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            // Упрощенная реализация foreach, считающая, что это массив или нечто, к чему можно обращаться по индексу
            // foreach (var item in collection) ->
            // var _collection = collection; int _i = 0; while(_i < _collection.Length) { var item = _collection[_i]; ... _i++; }

            // Здесь мы используем абстракцию (пока считаем, что Length = 0 или делаем условный переход)
            // По-хорошему нужно парсить GetEnumerator() или Array

            string startLabel = context.NextLabel("FOREACH_START");
            string endLabel = context.NextLabel("FOREACH_END");
            string incLabel = context.NextLabel("FOREACH_INC");

            _loopContexts.Push((endLabel, incLabel));

            var collectionType = context.SemanticModel.GetTypeInfo(node.Expression).Type;
            bool isArray = collectionType?.TypeKind == TypeKind.Array;

            // Вычисляем адрес коллекции
            int collectionReg = context.NextFreeRegister++;
            ASMInstructions.EmitExpression(node.Expression, collectionReg, context);

            // Индекс
            int indexReg = context.NextFreeRegister++;
            ASMInstructions.EmitMovImmediate(indexReg, 0, context);

            // Размер коллекции
            int lengthReg = context.NextFreeRegister++;

            if (isArray)
            {
                // Вычитаем 4 из указателя, чтобы прочитать Length
                int fourReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovImmediate(fourReg, 4, context);

                int lenAddrReg = context.NextFreeRegister++;
                ASMInstructions.EmitArithmeticOp(SyntaxKind.SubtractExpression, lenAddrReg, collectionReg, fourReg, context);
                context.Emit($"LDR r{lengthReg}, [r{lenAddrReg}, #0] @ Read array length");

                context.NextFreeRegister -= 2;
            }
            else
            {
                // Для IEnum/IList - не реализовано
                context.Emit($"@ TODO: IEnumerable foreach не реализован");
            }

            context.MarkLabel(startLabel);

            // Условие: index < length
            ASMInstructions.EmitCompare(indexReg, lengthReg, context);
            ASMInstructions.EmitBranch(endLabel, "GE", context);

            // Читаем элемент: item = collection[index]
            int itemReg = context.NextFreeRegister++;
            context.RegisterMap[node.Identifier.Text] = itemReg;

            if (isArray)
            {
                // collection + (index * 4)
                int offsetReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovRegister(offsetReg, indexReg, context);
                int elemSizeReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovImmediate(elemSizeReg, 4, context);
                ASMInstructions.EmitArithmeticOp(SyntaxKind.MultiplyExpression, offsetReg, offsetReg, elemSizeReg, context);

                int targetAddrReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovRegister(targetAddrReg, collectionReg, context);
                ASMInstructions.EmitArithmeticOp(SyntaxKind.AddExpression, targetAddrReg, targetAddrReg, offsetReg, context);

                context.Emit($"LDR r{itemReg}, [r{targetAddrReg}, #0]");
                context.NextFreeRegister -= 3;
            }

            // Тело
            Visit(node.Statement);

            context.MarkLabel(incLabel);

            // ++index
            ASMInstructions.EmitOpWithImmediate(SyntaxKind.AddExpression, indexReg, indexReg, 1, context);

            ASMInstructions.EmitJump(startLabel, context);

            context.MarkLabel(endLabel);

            _loopContexts.Pop();

            context.NextFreeRegister -= 3; // itemReg, lengthReg, indexReg, collectionReg
            context.NextFreeRegister--;
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
            context.MarkLabel(catchLabel);
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

            context.MarkLabel(endLabel);

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
            context.MarkLabel(startLabel);

            // 1. Вычисляем условие. 
            // Если оно ложно (false) — прыгаем сразу на выход (endLabel)
            // Если истинно — просто идем дальше в тело цикла
            ASMInstructions.EmitLogicalCondition(node.Condition, "", endLabel, context);

            // 2. Тело цикла: рекурсивно посещаем все стейтменты внутри { }
            Visit(node.Statement);

            // 3. Прыжок обратно на проверку условия
            ASMInstructions.EmitJump(startLabel, context);

            // Метка выхода
            context.MarkLabel(endLabel);

            _loopContexts.Pop();
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            string startLabel = context.NextLabel("DO_START");
            string endLabel = context.NextLabel("DO_END");
            string condLabel = context.NextLabel("DO_COND"); // Для continue

            _loopContexts.Push((endLabel, condLabel));

            context.MarkLabel(startLabel);

            Visit(node.Statement);

            context.MarkLabel(condLabel);

            // Проверяем условие. Если true -> прыгаем в начало (startLabel).
            ASMInstructions.EmitLogicalCondition(node.Condition, startLabel, endLabel, context);

            context.MarkLabel(endLabel);

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
                context.MarkLabel(sectionLabels[section]);
                foreach (var statement in section.Statements)
                {
                    Visit(statement);
                }
            }

            // Конец
            context.MarkLabel(endSwitchLabel);
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
            var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (symbolInfo is IFieldSymbol fieldSymbol)
            {
                string fieldName = fieldSymbol.Name;
                string typeName = fieldSymbol.ContainingType.ToDisplayString();

                if (context.Class.Global.Childs.TryGetValue(typeName, out var typeCtx) && typeCtx is TypeCompilationContext tcc)
                {
                    if (tcc.FieldOffsets.TryGetValue(fieldName, out int fieldOffset))
                    {
                        string structName = memberAccess.Expression.ToString();
                        
                        if (context.StackMap.TryGetValue(structName, out var stackVar))
                        {
                            ASMInstructions.EmitMemoryAccess(false, srcReg, 13, stackVar.StackOffset + fieldOffset, context);
                        }
                        else
                        {
                            int baseReg = context.NextFreeRegister++;
                            ASMInstructions.EmitExpression(memberAccess.Expression, baseReg, context, 0);
                            ASMInstructions.EmitMemoryAccess(false, srcReg, baseReg, fieldOffset, context);
                            context.NextFreeRegister--;
                        }
                    }
                    else
                    {
                        context.Emit($"@ Field {fieldName} not found in offsets for {typeName}");
                    }
                }
                else
                {
                    context.Emit($"@ Type {typeName} metadata not found for field {fieldName}");
                }
            }
        }
        private void HandleLocalAssignment(AssignmentExpressionSyntax node, int destReg, int srcReg)
        {
            bool isRef = false;
            if (node.Left is IdentifierNameSyntax id)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(id).Symbol;
                if (symbol is IParameterSymbol paramSymbol && 
                    (paramSymbol.RefKind == RefKind.Ref || paramSymbol.RefKind == RefKind.Out))
                {
                    isRef = true;
                }
            }

            if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                // Обычное x = y;
                if (isRef)
                {
                    // destReg contains a pointer, write srcReg to it
                    ASMInstructions.EmitMemoryAccess(false, srcReg, destReg, 0, context);
                }
                else if (destReg != srcReg)
                {
                    ASMInstructions.EmitMovRegister(destReg, srcReg, context);
                }
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
                    if (isRef)
                    {
                        int tmpReg = context.NextFreeRegister++;
                        ASMInstructions.EmitMemoryAccess(true, tmpReg, destReg, 0, context); // Load from pointer
                        ASMInstructions.EmitArithmeticOp(opKind, tmpReg, tmpReg, srcReg, context);
                        ASMInstructions.EmitMemoryAccess(false, tmpReg, destReg, 0, context); // Store to pointer
                        context.NextFreeRegister--;
                    }
                    else
                    {
                        // Выполняем операцию: Rdest = Rdest op Rsrc
                        ASMInstructions.EmitArithmeticOp(opKind, destReg, destReg, srcReg, context);
                    }
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
                var symbolInfo = context.SemanticModel.GetSymbolInfo(node.Left).Symbol;
                if (symbolInfo is IPropertySymbol propertySymbol && propertySymbol.SetMethod != null)
                {
                    // Нужно вызвать set_Property(value)

                    int regOffset = 0;
                    if (!propertySymbol.IsStatic)
                    {
                        // "this" вычисляем и кладем в R0
                        ASMInstructions.EmitExpression(memberAccess.Expression, 0, context);
                        regOffset = 1;
                    }

                    // Значение кладем в R1 (или R0, если статическое)
                    if (standardValueReg != regOffset)
                    {
                        context.Emit($"MOV r{regOffset}, r{standardValueReg}");
                        ASMInstructions.EmitMovRegister(regOffset, standardValueReg, context);
                    }

                    string nativeFunctionName = propertySymbol.SetMethod.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                        .ConstructorArguments.FirstOrDefault().Value?.ToString();

                    string callTarget = nativeFunctionName ?? propertySymbol.SetMethod.ToDisplayString();
                    ASMInstructions.EmitCall(callTarget, context, propertySymbol.IsStatic, nativeFunctionName != null);
                }
                else
                {
                    HandleStructAssignment(node, memberAccess, standardValueReg);
                }
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
            context.MarkLabel(trueLabel);
            Visit(node.Statement);
            ASMInstructions.EmitJump(endLabel, context);

            // Тело FALSE (или следующий Else If)
            context.MarkLabel(falseLabel);
            if (node.Else != null)
            {
                Visit(node.Else.Statement);
            }

            context.MarkLabel(endLabel);
        }
        //public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        //{
        //    if (context.ExceptTypes.Contains(node)) return;
        //    base.VisitClassDeclaration(node);
        //}

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // Метод может не возвращать значение, или мы игнорируем его
            // Обработчик в EmitExpression уже знает как сгенерировать BL и подготовить аргументы
            ASMInstructions.EmitExpression(node, 0, context);
        }

    }
}
