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
        public static void EmitArithmetic(BinaryExpressionSyntax node, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            // Левая часть: используем текущий свободный регистр (r0, r1...)
            int leftReg = GetOperandRegister(node.Left, context, tempOffset);

            // Правая часть: ОБЯЗАТЕЛЬНО используем СЛЕДУЮЩИЙ регистр
            int rightTemp = tempOffset + 1;

            if (node.Right is LiteralExpressionSyntax literal)
            {
                int value = ParseLiteral(literal,context);
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

        private static int GetOperandRegister(ExpressionSyntax expr, MethodCompilationContext context, int tempOffset)
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

        public static void EmitOpWithImmediate(SyntaxKind op, int target, int left, int value, MethodCompilationContext context)
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
        public static void EmitArithmeticOp(SyntaxKind op, int target, int left, int right, MethodCompilationContext context)
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

        public static void EmitMovRegister(int target, int source, MethodCompilationContext context)
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
        public static void EmitMovImmediate(int reg, int val, MethodCompilationContext context)
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
        public static void EmitDivide(int target, int left, int right, MethodCompilationContext context)
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

        public static void EmitLogicalCondition(ExpressionSyntax condition, string trueLabel, string falseLabel, MethodCompilationContext context)
        {
            if (condition is ParenthesizedExpressionSyntax paren)
            {
                EmitLogicalCondition(paren.Expression, trueLabel, falseLabel, context);
                return;
            }

            if (condition is BinaryExpressionSyntax bin)
            {
                if (bin.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    EmitLogicalCondition(bin.Left, trueLabel, "", context);
                    EmitLogicalCondition(bin.Right, trueLabel, falseLabel, context);
                    return;
                }
                if (bin.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    string nextAnd = $"L_AND_{context.LabelCount++}";
                    EmitLogicalCondition(bin.Left, nextAnd, falseLabel, context);
                    context.Asm.AppendLine($"{nextAnd}:");
                    EmitLogicalCondition(bin.Right, trueLabel, falseLabel, context);
                    return;
                }

                if (bin.IsKind(SyntaxKind.EqualsExpression) || bin.IsKind(SyntaxKind.NotEqualsExpression) ||
                    bin.IsKind(SyntaxKind.GreaterThanExpression) || bin.IsKind(SyntaxKind.LessThanExpression) ||
                    bin.IsKind(SyntaxKind.GreaterThanOrEqualExpression) || bin.IsKind(SyntaxKind.LessThanOrEqualExpression))
                {
                    int startReg = context.NextFreeRegister;
                    int leftReg = context.NextFreeRegister++;
                    EmitExpression(bin.Left, leftReg, context);

                    int rightReg = context.NextFreeRegister++;
                    EmitExpression(bin.Right, rightReg, context);

                    EmitCompare(leftReg, rightReg, context);

                    string op = bin.Kind() switch
                    {
                        SyntaxKind.EqualsExpression => "EQ",
                        SyntaxKind.NotEqualsExpression => "NE",
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
                        string invOp = op switch { "EQ" => "NE", "NE" => "EQ", "GT" => "LE", "LT" => "GE", "GE" => "LT", "LE" => "GT", _ => "NE" };
                        EmitBranch(falseLabel, invOp, context);
                    }

                    context.NextFreeRegister = startReg;
                    return;
                }
            }

            if (condition is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.LogicalNotExpression))
            {
                int startReg = context.NextFreeRegister;
                int reg = context.NextFreeRegister++;
                EmitExpression(unary.Operand, reg, context);
                EmitCompareImmediate(reg, 0, context);

                if (!string.IsNullOrEmpty(trueLabel))
                    EmitBranch(trueLabel, "EQ", context);
                if (!string.IsNullOrEmpty(falseLabel))
                    EmitBranch(falseLabel, "NE", context);

                context.NextFreeRegister = startReg;
                return;
            }

            // Fallback: evaluate anything as boolean expression directly
            int condStartReg = context.NextFreeRegister;
            int condReg = context.NextFreeRegister++;
            EmitExpression(condition, condReg, context);
            EmitCompareImmediate(condReg, 0, context);

            if (!string.IsNullOrEmpty(trueLabel))
                EmitBranch(trueLabel, "NE", context);
            if (!string.IsNullOrEmpty(falseLabel))
                EmitBranch(falseLabel, "EQ", context);

            context.NextFreeRegister = condStartReg;
        }
        public static void EmitExpression(ExpressionSyntax expr, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            var constOpt = context.SemanticModel.GetConstantValue(expr);
            if (constOpt.HasValue)
            {
                if (constOpt.Value == null)
                {
                    EmitMovImmediate(targetReg, 0, context);
                    return;
                }
                else if (constOpt.Value is string stringValue)
                {
                    var stringSymbol = context.Class.Global.RegisterStringLiteral(stringValue);
                    context.AddDataRelocation(stringSymbol);
                    context.Emit($"LDR r{targetReg}, ={stringSymbol} ; (placeholder for MOVW/MOVT)");
                    context.Bin.Write(new byte[8], 0, 8);
                    return;
                }
                else
                {
                    try
                    {
                        int val = Convert.ToInt32(constOpt.Value);
                        EmitMovImmediate(targetReg, val, context);
                        return;
                    }
                    catch { /* fallback if not an integer */ }
                }
            }

            if (expr is LiteralExpressionSyntax literal)
            {
                if (literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var stringValue = literal.Token.ValueText;
                    var compilationContext = context.Class.Global;

                    var stringSymbol = compilationContext.StringLiterals
                        .FirstOrDefault(kvp => kvp.Value == stringValue).Key;

                    if (stringSymbol == null)
                        throw new InvalidOperationException($"String literal '{stringValue}' was not found in the compilation context.");

                    context.AddDataRelocation(stringSymbol);
                    context.Emit($"LDR r{targetReg}, ={stringSymbol} ; (placeholder for MOVW/MOVT)");
                    context.Bin.Write(new byte[8], 0, 8);
                }
                else
                {
                    // Случай: var a = 10;
                    int value = ParseLiteral(literal, context);
                    EmitMovImmediate(targetReg, value, context);
                }
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
                if (binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                    binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                    binary.IsKind(SyntaxKind.EqualsExpression) ||
                    binary.IsKind(SyntaxKind.NotEqualsExpression) ||
                    binary.IsKind(SyntaxKind.GreaterThanExpression) ||
                    binary.IsKind(SyntaxKind.LessThanExpression) ||
                    binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) ||
                    binary.IsKind(SyntaxKind.LessThanOrEqualExpression))
                {
                    string trueLabel = context.NextLabel("BOOL_TRUE");
                    string falseLabel = context.NextLabel("BOOL_FALSE");
                    string endLabel = context.NextLabel("BOOL_END");

                    EmitLogicalCondition(binary, trueLabel, falseLabel, context);

                    context.Emit($"{trueLabel}:");
                    EmitMovImmediate(targetReg, 1, context);
                    EmitJump(endLabel, context);

                    context.Emit($"{falseLabel}:");
                    EmitMovImmediate(targetReg, 0, context);

                    context.Emit($"{endLabel}:");
                }
                else
                {
                    // ТЕПЕРЬ МЫ ЗАКРЫВАЕМ ЭТО:
                    EmitArithmetic(binary, targetReg, context, tempOffset);
                }
            }
            else if (expr is PrefixUnaryExpressionSyntax prefix)
            {
                if (prefix.IsKind(SyntaxKind.UnaryMinusExpression))
                {
                    EmitExpression(prefix.Operand, targetReg, context, tempOffset);
                    context.Emit($"RSBS r{targetReg}, r{targetReg}, #0");
                    context.Write16((ushort)(0x4240 | ((targetReg & 0x7) << 3) | (targetReg & 0x7))); // RSBS Rd, Rn, #0
                }
                else if (prefix.IsKind(SyntaxKind.UnaryPlusExpression))
                {
                    EmitExpression(prefix.Operand, targetReg, context, tempOffset);
                }
                else if (prefix.IsKind(SyntaxKind.LogicalNotExpression))
                {
                    // !x : Для bool (0 или 1) это x ^ 1
                    EmitExpression(prefix.Operand, targetReg, context, tempOffset);
                    int tmp = tempOffset + 1;
                    EmitMovImmediate(tmp, 1, context);
                    EmitArithmeticOp(SyntaxKind.ExclusiveOrExpression, targetReg, targetReg, tmp, context);
                }
                else if (prefix.IsKind(SyntaxKind.BitwiseNotExpression))
                {
                    EmitExpression(prefix.Operand, targetReg, context, tempOffset);
                    EmitArithmeticOp(SyntaxKind.BitwiseNotExpression, targetReg, targetReg, targetReg, context);
                }
                else if (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))
                {
                    if (prefix.Operand is IdentifierNameSyntax prefixId && context.RegisterMap.TryGetValue(prefixId.Identifier.Text, out int varReg))
                    {
                        var kind = prefix.IsKind(SyntaxKind.PreIncrementExpression) ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression;
                        EmitOpWithImmediate(kind, varReg, varReg, 1, context);
                        if (targetReg != varReg)
                            EmitMovRegister(targetReg, varReg, context);
                    }
                    else
                    {
                        // TODO: proper ref processing for prop/field/array increment
                        context.Emit($"@ TODO: complex prefix ++/-- not fully supported yet");
                    }
                }
            }
            else if (expr is PostfixUnaryExpressionSyntax postfix)
            {
                // Для постфикса возвращаем *старое* значение: r_target = old_val, varReg = old_val +/- 1
                if (postfix.Operand is IdentifierNameSyntax postfixId && context.RegisterMap.TryGetValue(postfixId.Identifier.Text, out int varReg))
                {
                    if (targetReg != varReg)
                        EmitMovRegister(targetReg, varReg, context);

                    var kind = postfix.IsKind(SyntaxKind.PostIncrementExpression) ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression;
                    EmitOpWithImmediate(kind, varReg, varReg, 1, context);
                }
                else
                {
                    // TODO: proper ref processing
                    context.Emit($"@ TODO: complex postfix ++/-- not fully supported yet");
                }
            }
            else if (expr is ElementAccessExpressionSyntax elementAccess)
            {
                // Загрузка адреса массива/указателя
                EmitExpression(elementAccess.Expression, targetReg, context, tempOffset);

                // Вычисление индекса в temp
                int indexReg = tempOffset + 1;
                var arg = elementAccess.ArgumentList.Arguments[0]; // Пока 1D массивы
                EmitExpression(arg.Expression, indexReg, context, indexReg);

                // Умножаем на размер элемента (пока считаем int/ptr = 4 байта)
                int sizeReg = indexReg + 1;
                EmitMovImmediate(sizeReg, 4, context);
                EmitArithmeticOp(SyntaxKind.MultiplyExpression, indexReg, indexReg, sizeReg, context);

                // Складываем адрес и смещение: addr = addr + (index * 4)
                EmitArithmeticOp(SyntaxKind.AddExpression, targetReg, targetReg, indexReg, context);

                // LDR Rd, [Rd]
                context.Emit($"LDR r{targetReg}, [r{targetReg}, #0]");
                context.Write16((ushort)(0x6800 | ((targetReg & 0x7) << 3) | (targetReg & 0x7))); // LDR Rt, [Rn, #0]
            }
            else if (expr is ParenthesizedExpressionSyntax paren)
                EmitExpression(paren.Expression, targetReg, context, tempOffset);
            else if (expr is SizeOfExpressionSyntax sizeOfExpr)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(sizeOfExpr.Type).Type;
                int size = 4; // Default reference/pointer/int size
                if (typeSymbol != null)
                {
                    if (typeSymbol.SpecialType == SpecialType.System_Byte || typeSymbol.SpecialType == SpecialType.System_SByte || typeSymbol.SpecialType == SpecialType.System_Boolean) size = 1;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int16 || typeSymbol.SpecialType == SpecialType.System_UInt16 || typeSymbol.SpecialType == SpecialType.System_Char) size = 2;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int64 || typeSymbol.SpecialType == SpecialType.System_UInt64 || typeSymbol.SpecialType == SpecialType.System_Double) size = 8;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int32 || typeSymbol.SpecialType == SpecialType.System_UInt32 || typeSymbol.SpecialType == SpecialType.System_Single) size = 4;
                    else if (typeSymbol.SpecialType == SpecialType.System_IntPtr || typeSymbol.SpecialType == SpecialType.System_UIntPtr) size = 4;
                    else if (typeSymbol.TypeKind == TypeKind.Struct)
                        size = Math.Max(1, typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4); // Simplistic struct size
                }
                EmitMovImmediate(targetReg, size, context);
            }
            else if (expr is TypeOfExpressionSyntax typeOfExpr)
            {
                // TODO: Return a pointer to type metadata object. For now we return 0 (null) or a stub id.
                context.Emit($"@ TODO: typeof({typeOfExpr.Type})");
                EmitMovImmediate(targetReg, 0, context);
            }
            else if (expr is CastExpressionSyntax castExpr)
            {
                // Для начала просто вычисляем внутреннее выражение (например, приведение типа/enum к int).
                // Сложные приведения ссылочных типов пока опускаем
                EmitExpression(castExpr.Expression, targetReg, context, tempOffset);
            }
            else if (expr is MemberAccessExpressionSyntax memberAccess)
            {
                // Проверяем, не является ли это константой (например, Enum: GPIO_Port.PortA)
                if (TryGetAsConstant(memberAccess, context, out object constVal))
                {
                    EmitMovImmediate(targetReg, Convert.ToInt32(constVal), context);
                    return;
                }

                // Иначе это чтение свойства объекта или поля (obj.Property или obj.Field)
                var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
                if (symbolInfo is IPropertySymbol propertySymbol)
                {
                    // Обращение к свойству транслируется в вызов метода get_Property
                    if (propertySymbol.GetMethod != null)
                    {
                        if (!propertySymbol.IsStatic)
                        {
                            // "this" для свойства
                            EmitExpression(memberAccess.Expression, 0, context, tempOffset);
                        }

                        string nativeFunctionName = propertySymbol.GetMethod.GetAttributes()
                            .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                            .ConstructorArguments.FirstOrDefault().Value?.ToString();

                        string callTarget = nativeFunctionName ?? propertySymbol.GetMethod.ToDisplayString();
                        EmitCall(callTarget, context, propertySymbol.IsStatic, nativeFunctionName != null);

                        if (targetReg != 0)
                        {
                            EmitMovRegister(targetReg, 0, context);
                        }
                    }
                }
                else if (symbolInfo is IFieldSymbol fieldSymbol)
                {
                    // Чтение поля из памяти (или стека, если структура)
                    // TODO: Полноценное чтение полей
                    string structName = memberAccess.Expression.ToString();
                    string fieldName = memberAccess.Name.ToString();

                    if (context.StackMap.TryGetValue(structName, out var stackVar))
                    {
                        if (stackVar.Metadata.FieldOffsets.TryGetValue(fieldName, out int fieldOffset))
                        {
                            context.Emit($"LDR r{targetReg}, [SP, #{stackVar.StackOffset + fieldOffset}] @ Load {structName}.{fieldName}");
                            // Binary TODO encoding
                        }
                    }
                }
            }
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
            else if (expr is ObjectCreationExpressionSyntax objectCreation)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(objectCreation.Type).Type;

                int size = 4; // minimum size
                if (typeSymbol != null)
                {
                    // Compute basic size for classes (just counting fields * 4)
                    int fieldsCount = typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count();
                    if (fieldsCount > 0) size = fieldsCount * 4;
                }

                EmitMovImmediate(0, size, context);

                // Call the native allocator (returns ptr in R0)
                EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

                // Store object pointer to targetReg
                if (targetReg != 0)
                {
                    EmitMovRegister(targetReg, 0, context);
                }

                // Call constructor if present
                var ctorSymbol = context.SemanticModel.GetSymbolInfo(objectCreation).Symbol as IMethodSymbol;
                if (ctorSymbol != null && !ctorSymbol.IsImplicitlyDeclared && objectCreation.ArgumentList != null)
                {
                    var args = objectCreation.ArgumentList.Arguments;

                    // Note: In AAPCS, r0 is "this", args are in r1-r3
                    // Because EmitExpression on args may clobber R0, we must preserve 'this' on stack or evaluate args carefully.
                    // For simplicity right now, assuming simple args.

                    // We need this pointer in R0. If we already moved it to targetReg above, let's put it back to R0.
                    // Wait, we need to load arguments inside r1-r3!
                    for (int i = 0; i < args.Count; i++)
                    {
                        EmitExpression(args[i].Expression, i + 1, context, tempOffset);
                    }

                    if (targetReg != 0) 
                    {
                        EmitMovRegister(0, targetReg, context);
                    }

                    string nativeFunctionName = ctorSymbol.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                        .ConstructorArguments.FirstOrDefault().Value?.ToString();

                    string callTarget = nativeFunctionName ?? ctorSymbol.ToDisplayString();
                    EmitCall(callTarget, context, isStatic: false, isNative: nativeFunctionName != null);
                }
            }
            else if (expr is ArrayCreationExpressionSyntax arrayCreation)
            {
                if (arrayCreation.Type.RankSpecifiers.Count > 0)
                {
                    int lengthReg = tempOffset;
                    int calcReg = tempOffset + 1;

                    if (arrayCreation.Initializer != null)
                    {
                        // Array from Initializer
                        int count = arrayCreation.Initializer.Expressions.Count;
                        EmitMovImmediate(lengthReg, count, context);
                    }
                    else
                    {
                        // Array from sizes
                        var rank = arrayCreation.Type.RankSpecifiers[0];
                        var sizeExpr = rank.Sizes[0];
                        EmitExpression(sizeExpr, lengthReg, context, tempOffset);
                    }

                    // multiply size by 4 bytes (assuming 32 bit structures/refs)
                    EmitMovImmediate(calcReg, 4, context);
                    EmitArithmeticOp(SyntaxKind.MultiplyExpression, calcReg, lengthReg, calcReg, context);

                    if (calcReg != 0) EmitMovRegister(0, calcReg, context);
                    EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

                    int arrPtrReg = targetReg != 0 ? targetReg : tempOffset + 2;
                    if (arrPtrReg != 0) EmitMovRegister(arrPtrReg, 0, context);

                    // Initialize array if any values provided
                    if (arrayCreation.Initializer != null)
                    {
                        var expressions = arrayCreation.Initializer.Expressions;
                        for (int i = 0; i < expressions.Count; i++)
                        {
                            int valReg = tempOffset + 3;
                            EmitExpression(expressions[i], valReg, context, valReg);

                            int offsetReg = tempOffset + 4;
                            EmitMovImmediate(offsetReg, i * 4, context);

                            int targetAddr = tempOffset + 5;
                            EmitMovRegister(targetAddr, arrPtrReg, context);
                            EmitArithmeticOp(SyntaxKind.AddExpression, targetAddr, targetAddr, offsetReg, context);

                            context.Emit($"STR r{valReg}, [r{targetAddr}, #0]");
                        }
                    }

                    if (targetReg != 0 && targetReg != arrPtrReg)
                    {
                        EmitMovRegister(targetReg, arrPtrReg, context);
                    }
                }
            }
            else if (expr is DefaultExpressionSyntax)
            {
                EmitMovImmediate(targetReg, 0, context);
            }
            else if (expr is InvocationExpressionSyntax invocation)
            {
                var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (methodSymbol == null) throw new Exception($"Символ не найден для вызова: {invocation}");

                var args = invocation.ArgumentList.Arguments;
                int regOffset = 0;

                if (!methodSymbol.IsStatic)
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax invokationMemberAccess)
                        EmitExpression(invokationMemberAccess.Expression, 0, context, tempOffset);
                    else
                        context.Emit("MOV r0, r4"); // fallback "this"
                    regOffset = 1;
                }

                for (int i = 0; i < args.Count; i++)
                {
                    var argument = args[i];
                    if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                    {
                        string varName = argument.Expression.ToString();
                        if (context.StackMap.TryGetValue(varName, out var stackVar))
                            context.Emit($"ADD R{i + regOffset}, SP, #{stackVar.StackOffset}");
                        continue;
                    }

                    int argReg = i + regOffset;
                    if (argReg > 3) throw new Exception("Поддерживается максимум 4 аргумента (включая this)");
                    EmitExpression(args[i].Expression, argReg, context, tempOffset);
                }

                var nativeAttr = methodSymbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true);
                string nativeFunctionName = nativeAttr?.ConstructorArguments.FirstOrDefault().Value?.ToString();
                string callTarget = nativeFunctionName ?? methodSymbol.ToDisplayString();

                EmitCall(callTarget, context, methodSymbol.IsStatic, nativeAttr != null);

                // Результат в R0 -> переносим по месту назначения
                if (targetReg != 0)
                {
                    EmitMovRegister(targetReg, 0, context);
                }
            }
        }

        public static void EmitFunctionFrame(MethodDeclarationSyntax node, MethodCompilationContext context, Action bodyBuilder)
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

        public static void EmitCompare(int leftReg, int rightReg, MethodCompilationContext context)
        {
            context.Emit($"CMP r{leftReg}, r{rightReg}");
            // Thumb-16: 0x4280 | (right << 3) | left
            ushort opcode = (ushort)(0x4280 | (rightReg << 3) | leftReg);
            context.Write16(opcode);
        }

        public static void EmitCompareImmediate(int reg, int value, MethodCompilationContext context)
        {
            context.Emit($"CMP r{reg}, #{value}");
            // Thumb-16: 0x2800 | (reg << 8) | (value & 0xFF)
            ushort opcode = (ushort)(0x2800 | (reg << 8) | (value & 0xFF));
            context.Write16(opcode);
        }
        public static void EmitCondition(ExpressionSyntax condition, string falseLabel, MethodCompilationContext context)
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
                        int val = ParseLiteral(literal, context);
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
        public static void EmitJump(string label, MethodCompilationContext context)
        {
            context.Emit($"B {label}");

            // Binary: Thumb-16 (Encoding T2)
            // Формат: [11100][imm11] -> 0xE000
            // Пока пишем заглушку 0x00 0xE0 (Little Endian)
            context.Bytecode(0x00);
            context.Bytecode(0xE0);
        }
        public static void EmitConditionalBranch(SyntaxKind conditionKind, string targetLabel, MethodCompilationContext context)
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
            context.Bytecode((byte)(0xD0)); // Код инструкции B<cc> (0xD0 - 0xDF)
        }
        public static void EmitBranch(string label, string condition, MethodCompilationContext context)
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

        public static int ParseLiteral(LiteralExpressionSyntax literal, MethodCompilationContext context)
        {
            var valueText = literal.Token.ValueText;

            // 1. Обработка булевых значений
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression)) return 1;
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression)) return 0;

            // 2. Обработка null (обычно 0)
            if (literal.IsKind(SyntaxKind.NullLiteralExpression)) return 0;
            if (literal.IsKind(SyntaxKind.DefaultLiteralExpression)) return 0;

            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return -1;
            }

            // 3. Обработка чисел (включая hex 0x...)
            try
            {
                if (valueText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(valueText, 16);

                return int.Parse(valueText);
            }
            catch (Exception ex)
            {
                throw new Exception($"Неподдерживаемый тип константы: {valueText} ({literal.Kind()})", ex);
            }
        }
        public static void EmitCall(string methodName, MethodCompilationContext context, bool isStatic, bool isNative)
        {
            context.Emit($"BL {methodName}");

            context.AddRelocation(methodName, isStatic, isNative);

            // Пишем 4 байта заглушки (0x00F0 0x00F8). 
            // Линковщик найдет их по оффсету из NativeRelocations и заменит на реальный оффсет.
            context.Write16(0xF000);
            context.Write16(0xF800);
        }

        public static void EmitMethodPrologue(bool isInstance, System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters, MethodCompilationContext context)
        {
            // Сохраняем регистры, которые мы будем использовать.
            context.Emit("PUSH {r4-r11, lr}");
            context.Write16(0xB5F0); // PUSH {r4-r7, lr} - нужно будет расширить до r11

            int argOffset = 0;
            if (isInstance)
            {
                // По стандарту ARM r0 - первый аргумент.
                // В методе экземпляра r0 всегда передает адрес объекта ('this').
                context.Emit("MOV r4, r0");
                context.RegisterMap["this"] = 4;
                context.NextFreeRegister = 5; // Начинаем выделять регистры для переменных с r5
                argOffset = 1; // Аргументы самого метода начнутся с r1
            }
            else
            {
                context.NextFreeRegister = 4; // В статических методах 'this' нет, начинаем с r4
            }

            // Сохраняем входящие аргументы из r0-r3 в r4-r11
            for (int i = 0; i < parameters.Length; i++)
            {
                int sourceReg = i + argOffset;
                if (sourceReg > 3)
                {
                    // Аргументы, которые не поместились в r0-r3, приходят через стек.
                    // Пока мы это не реализуем, но нужно иметь в виду.
                    break;
                }

                if (context.NextFreeRegister > 11)
                    throw new Exception("Недостаточно регистров для сохранения всех аргументов метода.");

                int destReg = context.NextFreeRegister++;
                string paramName = parameters[i].Name;

                // Генерируем инструкцию MOV для сохранения
                EmitMovRegister(destReg, sourceReg, context);
                context.Emit($"@ Save parameter '{paramName}' from r{sourceReg} to r{destReg}");

                // "Фиксируем" регистр за параметром
                context.RegisterMap[paramName] = destReg;
            }
        }
        public static void EmitMethodEpilogue(MethodCompilationContext context)
        {
            context.Emit($"{context.Name}_exit:"); // Метка для быстрых выходов (return)
            context.Emit("POP {r4-r11, pc}");

            // Бинарный код для POP {r4-r11, pc} 
            // r4-r11 (8 регистров) + PC = 9 бит в маске
            context.Write16(0xBDF0);

            context.Emit(".align 4"); // Выравнивание для следующей функции
        }

        public void EmitMethodFrame(MethodDeclarationSyntax node, MethodCompilationContext ctx, bool isInstance)
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

        public static bool TryGetAsConstant(ExpressionSyntax expr, MethodCompilationContext context, out object value)
        {
            value = 0;
            if (expr is LiteralExpressionSyntax literal) { value = ParseLiteral(literal, context); return true; }

            if (expr is IdentifierNameSyntax id && context.TryGetConstant(context.SemanticModel.GetSymbolInfo(expr).Symbol.ToDisplayString(), out value))
                return true;

            if (expr is MemberAccessExpressionSyntax ma && context.TryGetConstant(context.SemanticModel.GetSymbolInfo(expr).Symbol.ToDisplayString(), out value))
                return true;

            return false;
        }
        public static void PatchThumb2BL(byte[] binary, int offset, int jumpOffset)
        {
            // jumpOffset — это разница в байтах. BL прыгает по полсловам (halfwords).
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

        public static int GetOperand(ExpressionSyntax expr, MethodCompilationContext ctx, int targetReg)
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

        /// <summary>
        /// Патчит пару инструкций MOVW/MOVT для загрузки 32-битного значения в регистр r0.
        /// </summary>
        /// <param name="binary">Массив байт всей прошивки.</param>
        /// <param name="offset">Смещение до места, где начинается 8-байтовый плейсхолдер.</param>
        /// <param name="value">32-битный адрес, который нужно загрузить.</param>
        public static void PatchMovwMovt(byte[] binary, int offset, uint value)
        {
            // Эта реализация предполагает, что целевой регистр - r0.
            // MOVW r0, #lower_16_bits
            uint lower = value & 0xFFFF;
            uint movw = 0xF2400000 | (((lower >> 12) & 0xF) << 16) | (lower & 0xFFF);

            // MOVT r0, #upper_16_bits
            uint upper = (value >> 16) & 0xFFFF;
            uint movt = 0xF2C00000 | (((upper >> 12) & 0xF) << 16) | (upper & 0xFFF);

            // Записываем инструкции в little-endian формате
            binary[offset + 0] = (byte)movw;
            binary[offset + 1] = (byte)(movw >> 8);
            binary[offset + 2] = (byte)(movw >> 16);
            binary[offset + 3] = (byte)(movw >> 24);

            binary[offset + 4] = (byte)movt;
            binary[offset + 5] = (byte)(movt >> 8);
            binary[offset + 6] = (byte)(movt >> 16);
            binary[offset + 7] = (byte)(movt >> 24);
        }
    }
}
