using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Net;
using System.Text;

namespace NETMCUCompiler.CodeBuilder
{
    public class ASMInstructions
    {
        public static void EmitArithmetic(BinaryExpressionSyntax node, int targetReg, CompilationContext context, int tempOffset = 0)
        {
            // Левая часть: используем текущий свободный регистр (r0, r1...)
            int leftReg = GetOperandRegister(node.Left, context, tempOffset);

            // Правая часть: ОБЯЗАТЕЛЬНО используем СЛЕДУЮЩИЙ регистр
            int rightTemp = tempOffset + 1;

            if (node.Right is LiteralExpressionSyntax literal)
            {
                int value = ParseLiteral(literal);
                if (value >= 0 && value <= 7 && (node.IsKind(SyntaxKind.AddExpression) || node.IsKind(SyntaxKind.SubtractExpression)))
                {
                    EmitOpWithImmediate(node.Kind(), targetReg, leftReg, value, context);
                }
                else
                {
                    EmitMovImmediate(rightTemp, value, context);
                    EmitArithmeticOp(node.Kind(), targetReg, leftReg, rightTemp, context);
                }
            }
            else if (node.Right is IdentifierNameSyntax id)
            {
                // 1. Проверяем, константа ли это (d1, Program.d2)
                if (TryGetAsConstant(id, context, out object constVal))
                {
                    EmitMovImmediate(rightTemp, (int)constVal, context); // Грузим константу в r(rightTemp)
                    EmitArithmeticOp(node.Kind(), targetReg, leftReg, rightTemp, context);
                }
                // 2. Если это переменная (a, b...)
                else if (context.RegisterMap.TryGetValue(id.Identifier.Text, out int rReg))
                {
                    EmitArithmeticOp(node.Kind(), targetReg, leftReg, rReg, context);
                }
            }
            else
            {
                // Рекурсия: передаем rightTemp как целевой регистр И как новый оффсет
                EmitExpression(node.Right, rightTemp, context, rightTemp);
                EmitArithmeticOp(node.Kind(), targetReg, leftReg, rightTemp, context);
            }
        }

        private static int GetOperandRegister(ExpressionSyntax expr, CompilationContext context, int tempOffset)
        {
            if (expr is IdentifierNameSyntax id && context.RegisterMap.TryGetValue(id.Identifier.Text, out int reg)) return reg;

            if (TryGetAsConstant(expr, context, out object val))
            {
                EmitMovImmediate(tempOffset, (int)val, context);
                return tempOffset;
            }

            // Если это сложное выражение, вычисляем в текущий tempOffset
            EmitExpression(expr, tempOffset, context, tempOffset);
            return tempOffset;
        }

        private static void EmitOpWithImmediate(SyntaxKind op, int target, int left, int value, CompilationContext context)
        {
            // Thumb encoding T1 (3-bit immediate): ADDS Rd, Rn, #imm3
            // Формат: [000][11][10][imm3][Rn][Rd]
            string opName = op == SyntaxKind.AddExpression ? "ADDS" : "SUBS";
            context.Emit($"{opName} r{target}, r{left}, #{value}");

            ushort baseOp = (ushort)(op == SyntaxKind.AddExpression ? 0x1C00 : 0x1E00);
            ushort opcode = (ushort)(baseOp | ((value & 0x7) << 6) | (left << 3) | target);

            context.Bytecode((byte)(opcode & 0xFF));
            context.Bytecode((byte)(opcode >> 8));
        }
        public static void EmitArithmeticOp(SyntaxKind op, int target, int left, int right, CompilationContext context)
        {
            // 1. Умножение (Rd = Rd * Rm)
            if (op == SyntaxKind.MultiplyExpression)
            {
                if (target != left) EmitMovRegister(target, left, context);
                context.Emit($"MULS r{target}, r{right}");
                context.Write16((ushort)(0x4340 | ((right & 0x7) << 3) | (target & 0x7)));
                return;
            }

            // 2. Деление (32-bit SDIV)
            if (op == SyntaxKind.DivideExpression)
            {
                context.Emit($"SDIV r{target}, r{left}, r{right}");
                uint wideOp = 0xFB90F0F0 | (uint)((left & 0xF) << 16) | (uint)((target & 0xF) << 8) | (uint)(right & 0xF);
                context.Write32(wideOp);
                return;
            }

            // 3. Аккумулятивные операции (Логика и Сдвиги)
            // Эти инструкции в Thumb-16 работают только как Rd = Rd OP Rs
            bool isAccumulative = op == SyntaxKind.BitwiseAndExpression ||
                                 op == SyntaxKind.BitwiseOrExpression ||
                                 op == SyntaxKind.ExclusiveOrExpression ||
                                 op == SyntaxKind.LeftShiftExpression ||
                                 op == SyntaxKind.RightShiftExpression;

            if (isAccumulative)
            {
                if (target != left) EmitMovRegister(target, left, context);

                string name = op switch
                {
                    SyntaxKind.BitwiseAndExpression => "ANDS",
                    SyntaxKind.BitwiseOrExpression => "ORRS",
                    SyntaxKind.ExclusiveOrExpression => "EORS",
                    SyntaxKind.LeftShiftExpression => "LSLS",
                    _ => "LSRS"
                };
                ushort baseOp = op switch
                {
                    SyntaxKind.BitwiseAndExpression => (ushort)0x4000,
                    SyntaxKind.BitwiseOrExpression => (ushort)0x4300,
                    SyntaxKind.ExclusiveOrExpression => (ushort)0x4040,
                    SyntaxKind.LeftShiftExpression => (ushort)0x4080,
                    _ => (ushort)0x40C0
                };

                context.Emit($"{name} r{target}, r{right}");
                context.Write16((ushort)(baseOp | ((right & 0x7) << 3) | (target & 0x7)));
                return;
            }

            // 4. Трехрегистровые 16-бит операции (ADD/SUB) и Унарные
            switch (op)
            {
                case SyntaxKind.AddExpression:
                    context.Emit($"ADDS r{target}, r{left}, r{right}");
                    context.Write16((ushort)(0x1800 | (right << 6) | (left << 3) | target));
                    break;
                case SyntaxKind.SubtractExpression:
                    context.Emit($"SUBS r{target}, r{left}, r{right}");
                    context.Write16((ushort)(0x1A00 | (right << 6) | (left << 3) | target));
                    break;
                case SyntaxKind.BitwiseNotExpression:
                    context.Emit($"MVNS r{target}, r{left}");
                    context.Write16((ushort)(0x43C0 | ((left & 0x7) << 3) | (target & 0x7)));
                    break;
                case SyntaxKind.UnaryMinusExpression:
                    context.Emit($"RSBS r{target}, r{left}, #0");
                    context.Write16((ushort)(0x4240 | ((left & 0x7) << 3) | (target & 0x7)));
                    break;
            }
        }

        public static void EmitMovRegister(int target, int source, CompilationContext context)
        {
            // ASM: MOV r4, r5
            context.Emit($"MOV r{target}, r{source}");

            // BINARY: Thumb-16 encoding (T1)
            // Формат: [01000110][H1][H2][Rm:3][Rd:3]
            // H1 - бит старшего регистра для Rd, H2 - для Rm.
            ushort opcode = 0x4600;

            // Если целевой регистр (Rd) > 7, ставим бит H1 (бит 7)
            if (target > 7) opcode |= 0x0080;
            // Если исходный регистр (Rm) > 7, ставим бит H2 (бит 6)
            if (source > 7) opcode |= 0x0040;

            opcode |= (ushort)((source & 0x7) << 3); // Rm
            opcode |= (ushort)(target & 0x7);        // Rd

            context.Bytecode((byte)(opcode & 0xFF));
            context.Bytecode((byte)(opcode >> 8));
        }

        // Базовый MOV (Rd = Imm8)
        public static void EmitMovImmediate(int reg, int val, CompilationContext context)
        {
            if (val <= 255 && val >= 0)
            {
                context.Emit($"MOV r{reg}, #{val}");
                context.Write16((ushort)(0x2000 | (reg << 8) | (val & 0xFF)));
            }
            else
            {
                context.Emit($"MOVW r{reg}, #{val}");
                // Кодировка T3 для MOVW: 1111 0 i 10 0100 imm4 0 imm3 Rd imm8
                // Собираем аккуратно по битам ARM v7-M
                uint imm12 = (uint)(val & 0xFFFF);
                uint i = (imm12 >> 11) & 1;
                uint imm3 = (imm12 >> 8) & 7;
                uint imm8 = imm12 & 0xFF;
                uint imm4 = (imm12 >> 12) & 0xF;

                uint op = 0xF2400000;
                op |= (i << 26);
                op |= (imm4 << 16);
                op |= (imm3 << 12);
                op |= ((uint)reg << 8);
                op |= imm8;

                context.Write32(op);
            }
        }
        public static void EmitDivide(int target, int left, int right, CompilationContext context)
        {
            context.Emit($"SDIV r{target}, r{left}, r{right}");
            // Кодировка SDIV: 1111 1011 1001 [Rn] 1111 [Rd] 1111 [Rm]
            // Rn = left, Rd = target, Rm = right
            uint op = 0xFB90F0F0;
            op |= (uint)((left & 0xF) << 16);
            op |= (uint)((target & 0xF) << 8);
            op |= (uint)(right & 0xF);
            context.Write32(op);
        }

        public static void EmitLogicalCondition(ExpressionSyntax condition, string trueLabel, string falseLabel, CompilationContext context)
        {
            if (condition is ParenthesizedExpressionSyntax paren)
            {
                // Просто "ныряем" внутрь скобок, не меняя логику меток
                EmitLogicalCondition(paren.Expression, trueLabel, falseLabel, context);
                return;
            }

            if (condition is BinaryExpressionSyntax bin)
            {
                // Если это || (OR)
                if (bin.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    // Если левая часть истинна — сразу прыгаем в тело (True)
                    EmitLogicalCondition(bin.Left, trueLabel, "", context);
                    // Если нет — проверяем правую часть
                    EmitLogicalCondition(bin.Right, trueLabel, falseLabel, context);
                    return;
                }
                // Если это && (AND)
                if (bin.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    string nextAnd = $"L_AND_{context.LabelCount++}";
                    // Если левая часть ложна — прыгаем в конец (False)
                    EmitLogicalCondition(bin.Left, nextAnd, falseLabel, context);
                    context.Asm.AppendLine($"{nextAnd}:");
                    // Проверяем правую часть
                    EmitLogicalCondition(bin.Right, trueLabel, falseLabel, context);
                    return;
                }


                int rightVal = 0;
                string rightStr = bin.Right.ToString().Trim();

                // 1. Пытаемся понять, что справа: число или константа/enum
                if (bin.Right is LiteralExpressionSyntax literal)
                {
                    rightVal = ASMInstructions.ParseLiteral(literal);
                }
                else if (context.TryGetConstant(bin.Right, out var constVal))
                {
                    rightVal = (int)Convert.ToInt32(constVal);
                }
                else if (!int.TryParse(rightStr, out rightVal))
                {
                    // Если это не число и не константа, возможно это переменная?
                    // Тогда нужно генерировать CMP R, R, но пока просто кинем ошибку
                    throw new Exception($"Не удалось разрешить значение правой части: {rightStr}");
                }

                // Базовое сравнение (a == 11)
                var leftId = bin.Left as IdentifierNameSyntax;
                if (leftId != null)
                {
                    int leftReg = context.GetVarRegister(leftId.Identifier.Text);

                    EmitCompareImmediate(leftReg, rightVal, context);

                    // Генерируем прыжок
                    string op = bin.Kind() switch
                    {
                        SyntaxKind.EqualsExpression => "EQ",
                        SyntaxKind.GreaterThanExpression => "GT",
                        SyntaxKind.LessThanExpression => "LT",
                        SyntaxKind.GreaterThanOrEqualExpression => "GE",
                        SyntaxKind.LessThanOrEqualExpression => "LE",
                        _ => "EQ"
                    };

                    if (!string.IsNullOrEmpty(trueLabel))
                        EmitBranch(trueLabel, op, context);

                    if (!string.IsNullOrEmpty(falseLabel))
                    {
                        // Инвертируем для прыжка по лжи
                        string invOp = op switch { "EQ" => "NE", "GT" => "LE", "LT" => "GE", "GE" => "LT", "LE" => "GT", _ => "NE" };
                        EmitBranch(falseLabel, invOp, context);
                    }
                }
            }

            // ФИКС: Обработка одиночной переменной типа if (b)
            if (condition is IdentifierNameSyntax id)
            {
                int reg = context.GetVarRegister(id.Identifier.Text);
                EmitCompareImmediate(reg, 0, context);
                // Если не 0 (true) -> идем в TrueLabel, иначе в FalseLabel
                EmitBranch(trueLabel, "NE", context);
                if (!string.IsNullOrEmpty(falseLabel))
                    EmitBranch(falseLabel, "EQ", context);
                return;
            }

            // ФИКС: Обработка отрицания типа if (!b)
            if (condition is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.LogicalNotExpression))
            {
                if (unary.Operand is IdentifierNameSyntax innerId)
                {
                    int reg = context.GetVarRegister(innerId.Identifier.Text);
                    EmitCompareImmediate(reg, 0, context);
                    // Если 0 (false) -> значит !b это true, прыгаем в TrueLabel
                    EmitBranch(trueLabel, "EQ", context);
                    if (!string.IsNullOrEmpty(falseLabel))
                        EmitBranch(falseLabel, "NE", context);
                    return;
                }
            }
        }
        public static void EmitExpression(ExpressionSyntax expr, int targetReg, CompilationContext context, int tempOffset = 0)
        {
            if (expr is LiteralExpressionSyntax literal)
            {
                // Случай: var a = 10;
                int value = ParseLiteral(literal);
                EmitMovImmediate(targetReg, value, context);
            }
            else if (expr is IdentifierNameSyntax id)
            {
                // Случай: var a = b;
                if (context.RegisterMap.TryGetValue(id.Identifier.Text, out int srcReg))
                {
                    // Инструкция MOV Rd, Rm (копирование регистра в регистр)
                    // Thumb-16: 0x4600 | (src << 3) | target (с учетом High Registers флага, но r4-r11 влезают)
                    context.Emit($"MOV r{targetReg}, r{srcReg}");
                    ushort opcode = (ushort)(0x4600 | (srcReg << 3) | (targetReg & 0x7));
                    if (targetReg > 7) opcode |= 0x0080; // Коррекция для r8-r11 (High register bit)

                    context.Bytecode((byte)(opcode & 0xFF));
                    context.Bytecode((byte)(opcode >> 8));
                }
            }
            else if (expr is BinaryExpressionSyntax binary)
            {
                // ТЕПЕРЬ МЫ ЗАКРЫВАЕМ ЭТО:
                EmitArithmetic(binary, targetReg, context, tempOffset);
            }
            else if (expr is ParenthesizedExpressionSyntax paren)
                EmitExpression(paren.Expression, targetReg, context, tempOffset);
            else if (expr is ConditionalExpressionSyntax ternary)
            {
                // Создаем уникальные метки для этого конкретного тернарника
                string falseLabel = context.NextLabel("TERN_FALSE");
                string endLabel = context.NextLabel("TERN_END");

                // 1. Вычисляем условие. Если оно ложно — прыгаем на falseLabel
                // (Используем нашу готовую логику EmitLogicalCondition)
                EmitLogicalCondition(ternary.Condition, "", falseLabel, context);

                // 2. Ветка TRUE: вычисляем выражение и кладем результат в targetReg
                EmitExpression(ternary.WhenTrue, targetReg, context, tempOffset);

                // Прыгаем в конец, чтобы не выполнять ветку FALSE
                context.Emit($"B {endLabel}");

                // 3. Ветка FALSE
                context.Emit($"{falseLabel}:");
                EmitExpression(ternary.WhenFalse, targetReg, context, tempOffset);

                // 4. Финал
                context.Emit($"{endLabel}:");
            }
        }

        public static void EmitFunctionFrame(MethodDeclarationSyntax node, CompilationContext context, Action bodyBuilder)
        {
            var methodName = node.Identifier.Text;

            // --- ПРOЛОГ ---
            // ASM:
            context.Asm.AppendLine(".syntax unified");
            context.Asm.AppendLine(".thumb");
            context.Asm.AppendLine($".section .text.{methodName}, \"ax\"");
            context.Asm.AppendLine($".global {methodName}");
            context.Asm.AppendLine($"{methodName}:");
            context.Asm.AppendLine("    PUSH {r4-r11, lr}");

            // BINARY: Инструкция PUSH {r4-r11, lr} в Thumb-2
            // Код: 0xB5F0 (в Little Endian это F0 B5)
            context.Bytecode(0xF0);
            context.Bytecode(0xB5);


            // --- ТУТ БУДЕТ ТЕЛО (Шаг 2 и далее) ---
            // Пока оставляем место или передаем пустую строку/массив
            bodyBuilder();


            // --- ЭПИЛОГ ---
            // ASM:
            context.Asm.AppendLine($"{methodName}_exit:");
            context.Asm.AppendLine("    POP {r4-r11, pc}");
            context.Asm.AppendLine(".align 4");

            // BINARY: Инструкция POP {r4-r11, pc} в Thumb-2
            // Код: 0xBDF0 (в Little Endian это F0 BD)
            context.Bytecode(0xF0);
            context.Bytecode(0xBD);
        }

        public static void EmitCompare(int leftReg, int rightReg, CompilationContext context)
        {
            context.Emit($"CMP r{leftReg}, r{rightReg}");
            // Thumb-16: 0x4280 | (right << 3) | left
            ushort opcode = (ushort)(0x4280 | (rightReg << 3) | leftReg);
            context.Write16(opcode);
        }

        public static void EmitCompareImmediate(int reg, int value, CompilationContext context)
        {
            context.Emit($"CMP r{reg}, #{value}");
            // Thumb-16: 0x2800 | (reg << 8) | (value & 0xFF)
            ushort opcode = (ushort)(0x2800 | (reg << 8) | (value & 0xFF));
            context.Write16(opcode);
        }
        public static void EmitCondition(ExpressionSyntax condition, string falseLabel, CompilationContext context)
        {
            if (condition is BinaryExpressionSyntax binary)
            {
                // Если это логическое ИЛИ (||)
                if (binary.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    // Здесь нужна более сложная логика с промежуточными метками, 
                    // давай пока реализуем базовые сравнения
                    return;
                }

                // Если это простое сравнение (a == 11)
                if (binary.Left is IdentifierNameSyntax leftId)
                {
                    int leftReg = context.RegisterMap[leftId.Identifier.Text];

                    if (binary.Right is LiteralExpressionSyntax literal)
                    {
                        int val = ParseLiteral(literal);
                        EmitCompareImmediate(leftReg, val, context);
                    }

                    // Выбираем инструкцию инвертированного прыжка 
                    // (если условие НЕ ВЕРНО -> прыгаем в конец)
                    string jmpOp = binary.Kind() switch
                    {
                        SyntaxKind.EqualsExpression => "BNE",      // ==  -> прыжок если !=
                        SyntaxKind.NotEqualsExpression => "BEQ",   // !=  -> прыжок если ==
                        SyntaxKind.GreaterThanExpression => "BLE", // >   -> прыжок если <=
                        SyntaxKind.LessThanExpression => "BGE",    // <   -> прыжок если >=
                        SyntaxKind.GreaterThanOrEqualExpression => "BLT", // >= -> прыжок если <
                        SyntaxKind.LessThanOrEqualExpression => "BGT",    // <= -> прыжок если >
                        _ => "BNE"
                    };

                    context.Emit($"{jmpOp} {falseLabel}");
                    // В бинарник пишем 2 байта заглушки для относительного прыжка
                    context.Bytecode(0x00); // TODO: Реализовать патчинг адресов
                    context.Bytecode(0x00);// TODO: Реализовать патчинг адресов
                }
            }
        }
        public static void EmitJump(string label, CompilationContext context)
        {
            context.Emit($"B {label}");

            // Binary: Thumb-16 (Encoding T2)
            // Формат: [11100][imm11] -> 0xE000
            // Пока пишем заглушку 0x00 0xE0 (Little Endian)
            context.Bytecode(0x00);
            context.Bytecode(0xE0);
        }
        public static void EmitConditionalBranch(SyntaxKind conditionKind, string targetLabel, CompilationContext context)
        {
            // Выбираем операцию инвертированного перехода
            string jmpOp = conditionKind switch
            {
                SyntaxKind.EqualsExpression => "BNE",           // == -> прыгаем если !=
                SyntaxKind.NotEqualsExpression => "BEQ",        // != -> прыгаем если ==
                SyntaxKind.GreaterThanExpression => "BLE",      // >  -> прыгаем если <=
                SyntaxKind.LessThanExpression => "BGE",         // <  -> прыгаем если >=
                SyntaxKind.GreaterThanOrEqualExpression => "BLT",// >= -> прыгаем если <
                SyntaxKind.LessThanOrEqualExpression => "BGT",   // <= -> прыгаем если >
                _ => "BNE"
            };

            context.Emit($"{jmpOp} {targetLabel}");
            // Заглушка для бинарника (относительный переход в Thumb-16)
            context.Bytecode(0x00);
            context.Bytecode(0xD0); // Код инструкции B<cc> (0xD0 - 0xDF)
        }
        public static void EmitBranch(string label, string condition, CompilationContext context)
        {
            // condition может быть "EQ", "NE", "GT", "LT", "GE", "LE"
            context.Emit($"B{condition} {label}");

            // Binary: B<cc> это 0xD0 + код условия
            byte condCode = condition switch
            {
                "EQ" => 0,
                "NE" => 1,
                "GE" => 10,
                "LT" => 11,
                "GT" => 12,
                "LE" => 13,
                _ => 1
            };
            context.Bytecode(0x00); // Заглушка смещения
            context.Bytecode((byte)(0xD0 | condCode));
        }

        public static int ParseLiteral(LiteralExpressionSyntax literal)
        {
            var valueText = literal.Token.ValueText;

            // 1. Обработка булевых значений
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression)) return 1;
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression)) return 0;

            // 2. Обработка null (обычно 0)
            if (literal.IsKind(SyntaxKind.NullLiteralExpression)) return 0;

            // 3. Обработка чисел (включая hex 0x...)
            try
            {
                if (valueText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(valueText, 16);

                return int.Parse(valueText);
            }
            catch
            {
                throw new Exception("Неподдерживаемый тип константы");
            }
        }
        public static void EmitCall(string methodName, CompilationContext context, bool isStatic)
        {
            context.Emit($"BL {methodName}");

            context.AddRelocation(methodName, isStatic);

            // Пишем 4 байта заглушки (0x00F0 0x00F8). 
            // Линковщик найдет их по оффсету из NativeRelocations и заменит на реальный оффсет.
            context.Write16(0xF000);
            context.Write16(0xF800);
        }

        public static void EmitMethodPrologue(bool isInstance, CompilationContext context)
        {
            // Сохраняем регистры. r4 будет нашим "this" внутри функции.
            context.Emit("PUSH {r4-r11, lr}");

            if (isInstance)
            {
                // По стандарту ARM r0 - первый аргумент. 
                // В экземпляре класса r0 всегда передает адрес объекта.
                context.Emit("MOV r4, r0");
                context.RegisterMap["this"] = 4; // Закрепляем r4 за контекстом объекта
            }
        }
        public static void EmitMethodEpilogue(CompilationContext context)
        {
            context.Emit("Main_exit:"); // Метка для быстрых выходов (return)
            context.Emit("POP {r4-r11, pc}");

            // Бинарный код для POP {r4-r11, pc} 
            // r4-r11 (8 регистров) + PC = 9 бит в маске
            context.Write16(0xBDF0);

            context.Emit(".align 4"); // Выравнивание для следующей функции
        }

        public void EmitMethodFrame(MethodDeclarationSyntax node, CompilationContext ctx, bool isInstance)
        {
            ctx.Emit($"PUSH {{r4-r11, lr}}");
            if (isInstance)
            {
                // Перекладываем указатель на объект из r0 в r4 (сохраняемый регистр)
                // Теперь r4 — это наш неизменный 'this' на протяжении всей функции
                ctx.Emit("MOV r4, r0");
                ctx.RegisterMap["this"] = 4;
            }
            // ... остальная логика тела ...
        }

        public static bool TryGetAsConstant(ExpressionSyntax expr, CompilationContext context, out object value)
        {
            value = 0;
            if (expr is LiteralExpressionSyntax literal) { value = ParseLiteral(literal); return true; }

            if (expr is IdentifierNameSyntax id && context.TryGetConstant(context.SemanticModel.GetSymbolInfo(expr).Symbol.ToDisplayString(), out value))
                return true;

            if (expr is MemberAccessExpressionSyntax ma && context.TryGetConstant(context.SemanticModel.GetSymbolInfo(expr).Symbol.ToDisplayString(), out value))
                return true;

            return false;
        }
        public static void PatchThumb2BL(byte[] binary, int offset, int jumpOffset)
        {
            // jumpOffset — это разница в байтах. BL прыгает по полусловам (halfwords).
            int val = jumpOffset >> 1;

            int sign = (val >> 23) & 1;
            int j1 = (val >> 22) & 1;
            int j2 = (val >> 21) & 1;
            int imm10 = (val >> 11) & 0x3FF;
            int imm11 = val & 0x7FF;

            // Вычисляем биты I1 и I2 (инвертированные J через знак)
            int i1 = (j1 ^ sign) ^ 1;
            int i2 = (j2 ^ sign) ^ 1;

            // Первое полуслово: 1111 0 S imm10
            ushort high = (ushort)(0xF000 | (sign << 10) | imm10);
            // Второе полуслово: 11 0 1 I1 I2 imm11 (0xD... - это BL)
            ushort low = (ushort)(0xD000 | (i1 << 13) | (i2 << 11) | imm11);

            // Записываем в Little-Endian
            binary[offset] = (byte)(high & 0xFF);
            binary[offset + 1] = (byte)(high >> 8);
            binary[offset + 2] = (byte)(low & 0xFF);
            binary[offset + 3] = (byte)(low >> 8);
        }

        public static int GetOperand(ExpressionSyntax expr, CompilationContext ctx, int targetReg)
        {
            var symbol = ctx.SemanticModel.GetSymbolInfo(expr).Symbol;

            // 1. Это константа?
            if (ctx.TryGetConstant(symbol?.ToDisplayString(), out var val))
            {
                EmitMovImmediate(targetReg, (int)val, ctx);
                return targetReg;
            }

            // 2. Это локальная переменная?
            if (ctx.RegisterMap.TryGetValue(expr.ToString(), out int reg)) return reg;

            // 3. Это структура на стеке?
            if (ctx.StackMap.TryGetValue(expr.ToString(), out var stackVar))
            {
                ctx.Emit($"LDR R{targetReg}, [SP, #{stackVar.StackOffset}]");
                return targetReg;
            }

            throw new Exception($"Не удалось разрешить операнд: {expr}");
        }
    }
}
