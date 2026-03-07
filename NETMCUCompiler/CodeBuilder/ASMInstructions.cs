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
        private static void EmitDelegateCreation(IMethodSymbol targetMethod, ExpressionSyntax nodeExpr, ITypeSymbol delegateType, int targetReg, MethodCompilationContext context, int tempOffset)
        {
            int size = context.Class.Global.BuildingContext.Options?.TypeHeader == true ? 12 : 8; // delegate size is 8 bytes + 4 for header
            EmitMovImmediate(0, size, context);

            // Call the native allocator (returns ptr in R0)
            EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

            bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
            if (typeHeader)
            {
                string targetName = delegateType.ToDisplayString();
                string symbolName = context.Class.Global.RegisterTypeLiteral(delegateType);
                context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                int tmpReg = context.NextFreeRegister++;
                context.Emit($"@ Write TypeHeader for delegate {targetName}");
                context.Emit($"LDR r{tmpReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                context.Bin.Write(new byte[8], 0, 8);

                context.Emit($"STR r{tmpReg}, [r0, #0]"); // Put type ptr at beginning of allocation
                context.NextFreeRegister--;
            }

            int targetOffset = typeHeader ? 4 : 0;
            int ptrOffset = targetOffset + 4;
            if (context.Class.Global.Childs.TryGetValue("System.Delegate", out var delTypeCtx) && delTypeCtx is TypeCompilationContext delTcc)
            {
                delTcc.FieldOffsets.TryGetValue("Target", out targetOffset);
                delTcc.FieldOffsets.TryGetValue("MethodPtr", out ptrOffset);
            }

            int delObjReg = targetReg == 0 ? 0 : targetReg;
            if (targetReg != 0) EmitMovRegister(delObjReg, 0, context);

            int methodValReg = context.NextFreeRegister++;
            string methodName = targetMethod.ToDisplayString();
            context.Class.Global.AddRelocation(context, methodName, targetMethod.IsStatic, false, (int)context.Bin.Length);
            context.Emit($"LDR r{methodValReg}, ={methodName} ; (placeholder for address)");
            context.Bin.Write(new byte[8], 0, 8);

            context.Emit($"STR r{methodValReg}, [r{delObjReg}, #{ptrOffset}]");

            int targetValReg = context.NextFreeRegister++;
            if (targetMethod.IsStatic)
            {
                EmitMovImmediate(targetValReg, 0, context);
            }
            else
            {
                if (nodeExpr is MemberAccessExpressionSyntax delMemberAccess)
                {
                    EmitExpression(delMemberAccess.Expression, targetValReg, context, tempOffset);
                }
                else
                {
                    // Implicit this
                    context.Emit($"MOV r{targetValReg}, r4");
                }
            }
            context.Emit($"STR r{targetValReg}, [r{delObjReg}, #{targetOffset}]");

            context.NextFreeRegister -= 2;
        }

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

        public static void EmitLdrImmediate(int target, int offset, MethodCompilationContext context)
        {
            // Placeholder: currently not strictly needed without baseReg. Actually, let's just make EmitMemoryAccess.
        }

        public static void EmitAddressOf(ExpressionSyntax expr, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(expr).Symbol;

            if (expr is IdentifierNameSyntax id)
            {
                string varName = id.Identifier.Text;
                if (context.StackMap.TryGetValue(varName, out var stackVar))
                {
                    int offset = stackVar.StackOffset;
                    context.Emit($"ADD r{targetReg}, SP, #{offset}");
                    
                    // T2 ADD.W: 1111_0i10_0000_1101_0_imm3_Rd_imm8
                    // We'll use Thumb-2 wide instruction for ADD Rd, SP, #imm12
                    uint op = 0xF20D0000u;
                    op |= ((uint)targetReg & 0xF) << 8;
                    // imm12 formatting
                    uint i = ((uint)offset >> 11) & 1;
                    uint imm3 = ((uint)offset >> 8) & 7;
                    uint imm8 = (uint)offset & 0xFF;
                    op |= (i << 26) | (imm3 << 12) | imm8;
                    context.Write32(op);
                    return;
                }
            }
            else if (expr is MemberAccessExpressionSyntax memberAccess && symbolInfo is IFieldSymbol fieldSymbol)
            {
                string typeName = fieldSymbol.ContainingType.ToDisplayString();
                if (context.Class.Global.Childs.TryGetValue(typeName, out var typeCtx) && typeCtx is TypeCompilationContext tcc)
                {
                    if (tcc.FieldOffsets.TryGetValue(fieldSymbol.Name, out int fieldOffset))
                    {
                        string structName = memberAccess.Expression.ToString();
                        int baseReg = tempOffset + 1;

                        if (context.StackMap.TryGetValue(structName, out var stackVar))
                        {
                            // SP + stackVar.StackOffset + fieldOffset
                            int totalOffset = stackVar.StackOffset + fieldOffset;
                            context.Emit($"ADD r{targetReg}, SP, #{totalOffset}");
                            
                            uint op = 0xF20D0000u;
                            op |= ((uint)targetReg & 0xF) << 8;
                            uint i = ((uint)totalOffset >> 11) & 1;
                            uint imm3 = ((uint)totalOffset >> 8) & 7;
                            uint imm8 = (uint)totalOffset & 0xFF;
                            op |= (i << 26) | (imm3 << 12) | imm8;
                            context.Write32(op);
                        }
                        else
                        {
                            EmitExpression(memberAccess.Expression, baseReg, context, tempOffset + 1);
                            EmitOpWithImmediate(SyntaxKind.AddExpression, targetReg, baseReg, fieldOffset, context);
                        }
                        return;
                    }
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

                int sizeReg = indexReg + 1;
                EmitMovImmediate(sizeReg, 4, context);
                EmitArithmeticOp(SyntaxKind.MultiplyExpression, indexReg, indexReg, sizeReg, context);

                // Складываем адрес и смещение: addr = addr + (index * 4)
                EmitArithmeticOp(SyntaxKind.AddExpression, targetReg, targetReg, indexReg, context);
                return;
            }

            context.Emit($"@ TODO: EmitAddressOf not fully supported for {expr}");
            EmitMovImmediate(targetReg, 0, context);
        }

        public static void EmitMemoryAccess(bool isLoad, int targetReg, int baseReg, int offset, MethodCompilationContext context)
        {
            if (baseReg == 13) // SP
            {
                context.Emit($"{(isLoad ? "LDR" : "STR")} r{targetReg}, [SP, #{offset}]");
            }
            else
            {
                context.Emit($"{(isLoad ? "LDR" : "STR")} r{targetReg}, [r{baseReg}, #{offset}]");
            }

            // T3 32-bit encoding
            // LDR: 1111_1000_1101_Rn_Rt_imm12 -> 0xF8D0 | (Rn) << 16 | (Rt) << 12 | imm12
            // STR: 1111_1000_1100_Rn_Rt_imm12 -> 0xF8C0 | (Rn) << 16 | (Rt) << 12 | imm12
            uint op = isLoad ? 0xF8D00000u : 0xF8C00000u;
            op |= ((uint)baseReg & 0xF) << 16;
            op |= ((uint)targetReg & 0xF) << 12;
            op |= ((uint)offset & 0xFFF);

            context.Write32(op);
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
                    context.MarkLabel(nextAnd);
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
            var typeInfo = context.SemanticModel.GetTypeInfo(expr);
            var conversion = context.SemanticModel.GetConversion(expr);

            if (conversion.IsBoxing)
            {
                Console.WriteLine($"[DEBUG-CONV] BOXING on {expr}");
                int valReg = context.NextFreeRegister++;
                EmitExpressionInternal(expr, valReg, context, tempOffset);

                var typeSymbol = typeInfo.Type;
                int size = 4;
                if (typeSymbol != null)
                {
                    if (typeSymbol.SpecialType == SpecialType.System_Byte || typeSymbol.SpecialType == SpecialType.System_SByte || typeSymbol.SpecialType == SpecialType.System_Boolean) size = 1;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int16 || typeSymbol.SpecialType == SpecialType.System_UInt16 || typeSymbol.SpecialType == SpecialType.System_Char) size = 2;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int64 || typeSymbol.SpecialType == SpecialType.System_UInt64 || typeSymbol.SpecialType == SpecialType.System_Double) size = 8;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int32 || typeSymbol.SpecialType == SpecialType.System_UInt32 || typeSymbol.SpecialType == SpecialType.System_Single || typeSymbol.SpecialType == SpecialType.System_IntPtr) size = 4;
                    else if (typeSymbol.TypeKind == TypeKind.Struct)
                        size = Math.Max(1, typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                int allocSize = size + 4; // Header + payload
                EmitMovImmediate(0, allocSize, context);
                EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

                if (typeSymbol != null && context.Class.Global.BuildingContext.Options?.TypeHeader == true)
                {
                    string targetName = typeSymbol.ToDisplayString();
                    string symbolName = context.Class.Global.RegisterTypeLiteral(typeSymbol);
                    context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                    int tmpReg = context.NextFreeRegister++;
                    context.Emit($"@ Write TypeHeader for Boxed {targetName}");
                    context.Emit($"LDR r{tmpReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                    context.Bin.Write(new byte[8], 0, 8);
                    context.Emit($"STR r{tmpReg}, [r0, #0]");
                    context.NextFreeRegister--;
                }

                if (size == 1) context.Emit($"STRB r{valReg}, [r0, #4]");
                else if (size == 2) context.Emit($"STRH r{valReg}, [r0, #4]");
                else context.Emit($"STR r{valReg}, [r0, #4]");

                if (targetReg != 0) EmitMovRegister(targetReg, 0, context);
                context.NextFreeRegister--;
                return;
            }

            if (conversion.IsUnboxing) // Wait, if the cast expression is Unboxing?
            {
                Console.WriteLine($"[DEBUG-CONV] UNBOXING on {expr}");
                // Unboxing means we have an object reference (with a header) and want its payload
                int objReg = context.NextFreeRegister++;
                EmitExpressionInternal(expr, objReg, context, tempOffset);

                // For simplicity, we assume we just read offset 4 (after type header)
                var destType = typeInfo.ConvertedType;
                int size = 4;
                if (destType != null)
                {
                    if (destType.SpecialType == SpecialType.System_Byte || destType.SpecialType == SpecialType.System_SByte || destType.SpecialType == SpecialType.System_Boolean) size = 1;
                    else if (destType.SpecialType == SpecialType.System_Int16 || destType.SpecialType == SpecialType.System_UInt16 || destType.SpecialType == SpecialType.System_Char) size = 2;
                }

                if (size == 1) context.Emit($"LDRB r{targetReg}, [r{objReg}, #4]");
                else if (size == 2) context.Emit($"LDRH r{targetReg}, [r{objReg}, #4]");
                else context.Emit($"LDR r{targetReg}, [r{objReg}, #4]");

                context.NextFreeRegister--;
                return;
            }

            var typeSymbolInfo = context.SemanticModel.GetTypeInfo(expr);
            if (expr is CastExpressionSyntax ce)
            {
                var inputType = context.SemanticModel.GetTypeInfo(ce.Expression).Type;
                var targetType = context.SemanticModel.GetTypeInfo(ce.Type).Type;

                if (inputType != null && targetType != null)
                {
                    var cc = context.SemanticModel.Compilation.ClassifyConversion(inputType, targetType);
                    if (cc.IsUnboxing)
                    {
                        Console.WriteLine($"[DEBUG-CONV] CAST UNBOXING on {expr} from {inputType.Name} to {targetType.Name}");
                        EmitExpressionInternal(ce.Expression, targetReg, context, tempOffset);
                        context.Emit($"LDR r{targetReg}, [r{targetReg}, #4] @ CastUnbox Extract Payload");
                        return;
                    }
                }
            }

            EmitExpressionInternal(expr, targetReg, context, tempOffset);
        }

        private static void EmitExpressionInternal(ExpressionSyntax expr, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(expr);
            var possibleMethod = symbolInfo.Symbol as IMethodSymbol ?? context.SemanticModel.GetMemberGroup(expr).FirstOrDefault() as IMethodSymbol;

            if (possibleMethod != null && expr.Parent is not InvocationExpressionSyntax)
            {
                var typeInfo = context.SemanticModel.GetTypeInfo(expr);
                var convertedType = typeInfo.ConvertedType;
                if (convertedType != null && convertedType.TypeKind == TypeKind.Delegate)
                {
                    EmitDelegateCreation(possibleMethod, expr, convertedType, targetReg, context, tempOffset);
                    return;
                }
            }

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
                    context.Bytecode((byte)targetReg);
                    context.Bin.Write(new byte[7], 0, 7);
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
                    context.Bytecode((byte)targetReg);
                    context.Bin.Write(new byte[7], 0, 7);
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
                    bool isRef = false;
                    var symbol = context.SemanticModel.GetSymbolInfo(id).Symbol;
                    if (symbol is IParameterSymbol paramSymbol && 
                        (paramSymbol.RefKind == RefKind.Ref || paramSymbol.RefKind == RefKind.Out))
                    {
                        isRef = true;
                    }

                    if (isRef)
                    {
                        // Dereference the pointer: LDR targetReg, [srcReg, #0]
                        EmitMemoryAccess(true, targetReg, srcReg, 0, context);
                    }
                    else
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
            }
            else if (expr is BinaryExpressionSyntax binary)
            {
                if (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression))
                {
                    bool isAs = binary.IsKind(SyntaxKind.AsExpression);
                    var targetType = context.SemanticModel.GetTypeInfo(binary.Right).Type;

                    int objReg = context.NextFreeRegister++;
                    EmitExpressionInternal(binary.Left, objReg, context, tempOffset);

                    string endLabel = context.NextLabel(isAs ? "AS_END" : "IS_END");
                    string falseLabel = context.NextLabel(isAs ? "AS_FALSE" : "IS_FALSE");

                    EmitCompareImmediate(objReg, 0, context);
                    EmitBranch(falseLabel, "EQ", context);

                    if (targetType != null && context.Class.Global.BuildingContext.Options?.TypeHeader == true)
                    {
                        int typeReg = context.NextFreeRegister++;
                        context.Emit($"LDR r{typeReg}, [r{objReg}, #0] @ read TypeHeader");

                        string symbolName = context.Class.Global.RegisterTypeLiteral(targetType);
                        context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                        int targetTypeReg = context.NextFreeRegister++;
                        context.Emit($"LDR r{targetTypeReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                        context.Bin.Write(new byte[8], 0, 8);

                        EmitCompare(typeReg, targetTypeReg, context);
                        EmitBranch(falseLabel, "NE", context);

                        context.NextFreeRegister -= 2;

                        if (isAs)
                        {
                            if (targetReg != objReg) EmitMovRegister(targetReg, objReg, context);
                        }
                        else
                        {
                            EmitMovImmediate(targetReg, 1, context);
                        }
                        EmitJump(endLabel, context);
                    }
                    else
                    {
                        EmitJump(falseLabel, context);
                    }

                    context.MarkLabel(falseLabel);
                    EmitMovImmediate(targetReg, 0, context);

                    context.MarkLabel(endLabel);
                    context.NextFreeRegister--;
                }
                else if (binary.IsKind(SyntaxKind.LogicalAndExpression) ||
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

                    context.MarkLabel(trueLabel);
                    EmitMovImmediate(targetReg, 1, context);
                    EmitJump(endLabel, context);

                    context.MarkLabel(falseLabel);
                    EmitMovImmediate(targetReg, 0, context);

                    context.MarkLabel(endLabel);
                }
                else
                {
                    // ТЕПЕРЬ МЫ ЗАКРЫВАЕМ ЭТО:
                    EmitArithmetic(binary, targetReg, context, tempOffset);
                }
            }
            else if (expr is IsPatternExpressionSyntax isPattern)
            {
                ITypeSymbol targetType = null;
                if (isPattern.Pattern is DeclarationPatternSyntax declPattern) targetType = context.SemanticModel.GetTypeInfo(declPattern.Type).Type;
                else if (isPattern.Pattern is TypePatternSyntax typePattern) targetType = context.SemanticModel.GetTypeInfo(typePattern.Type).Type;
                else targetType = context.SemanticModel.GetTypeInfo(isPattern.Pattern).Type ?? context.SemanticModel.GetTypeInfo(isPattern.Pattern).ConvertedType;

                int objReg = context.NextFreeRegister++;
                EmitExpressionInternal(isPattern.Expression, objReg, context, tempOffset);

                string endLabel = context.NextLabel("IS_PAT_END");
                string falseLabel = context.NextLabel("IS_PAT_FALSE");

                EmitCompareImmediate(objReg, 0, context);
                EmitBranch(falseLabel, "EQ", context);

                if (targetType != null && context.Class.Global.BuildingContext.Options?.TypeHeader == true)
                {
                    int typeReg = context.NextFreeRegister++;
                    context.Emit($"LDR r{typeReg}, [r{objReg}, #0] @ read TypeHeader");

                    string symbolName = context.Class.Global.RegisterTypeLiteral(targetType);
                    context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                    int targetTypeReg = context.NextFreeRegister++;
                    context.Emit($"LDR r{targetTypeReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                    context.Bin.Write(new byte[8], 0, 8);

                    EmitCompare(typeReg, targetTypeReg, context);
                    EmitBranch(falseLabel, "NE", context);

                    context.NextFreeRegister -= 2;

                    EmitMovImmediate(targetReg, 1, context);
                    EmitJump(endLabel, context);
                }
                else
                {
                    EmitJump(falseLabel, context);
                }

                context.MarkLabel(falseLabel);
                EmitMovImmediate(targetReg, 0, context);

                context.MarkLabel(endLabel);
                context.NextFreeRegister--;

                if (isPattern.Pattern is DeclarationPatternSyntax dPattern && dPattern.Designation is SingleVariableDesignationSyntax singleVar)
                {
                    var symbol = context.SemanticModel.GetDeclaredSymbol(singleVar);
                    if (symbol != null && context.StackMap.TryGetValue(symbol.Name, out var stackVar))
                    {
                        context.MarkLabel(context.NextLabel("PAT_DEF_ASSIGN"));
                        // Pattern assigns variable implicitly on TRUE condition:
                        // Instead of full flow rewriting, let's inject simple assign if true? 
                        // But wait! If 'is' is false, Pattern does not assign. We need a more advanced architecture 
                        // for scope blocks. We leave assigning to complex pattern matching for later.
                    }
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
                        var kind = prefix.IsKind(SyntaxKind.PreIncrementExpression) ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression;
                        int addrReg = tempOffset + 1;
                        EmitAddressOf(prefix.Operand, addrReg, context, tempOffset + 1);

                        EmitMemoryAccess(true, targetReg, addrReg, 0, context);
                        EmitOpWithImmediate(kind, targetReg, targetReg, 1, context);
                        EmitMemoryAccess(false, targetReg, addrReg, 0, context);
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
                    var kind = postfix.IsKind(SyntaxKind.PostIncrementExpression) ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression;
                    int addrReg = tempOffset + 1;
                    EmitAddressOf(postfix.Operand, addrReg, context, tempOffset + 1);

                    EmitMemoryAccess(true, targetReg, addrReg, 0, context);

                    int tmpReg = tempOffset + 2;
                    EmitMovRegister(tmpReg, targetReg, context);
                    EmitOpWithImmediate(kind, tmpReg, tmpReg, 1, context);
                    EmitMemoryAccess(false, tmpReg, addrReg, 0, context);
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
                var typeSymbol = context.SemanticModel.GetSymbolInfo(typeOfExpr.Type).Symbol as ITypeSymbol;
                if (typeSymbol != null)
                {
                    string level = context.Class.Global.BuildingContext.Options?.TypeMetaDataLevel ?? "Full";
                    bool keepMetadata = level == "Full";
                    if (level == "Custom")
                    {
                        keepMetadata = typeSymbol.GetAttributes().Any(a => a.AttributeClass?.Name == "KeepMetadataAttribute" || a.AttributeClass?.Name == "KeepMetadata");
                    }

                    if (keepMetadata)
                    {
                        string targetName = typeSymbol.ToDisplayString();
                        string symbolName = context.Class.Global.RegisterTypeLiteral(typeSymbol);
                        context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                        context.Emit($"@ typeof({targetName})");
                        context.Emit($"LDR r{targetReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                        context.Bin.Write(new byte[8], 0, 8);
                    }
                    else
                    {
                        context.Emit($"@ typeof({typeSymbol.ToDisplayString()}) -> NULL (Metadata Level: {level})");
                        EmitMovImmediate(targetReg, 0, context);
                    }
                }
                else
                {
                    context.Emit($"@ typeof({typeOfExpr.Type}) -> NULL");
                    EmitMovImmediate(targetReg, 0, context);
                }
            }
            else if (expr is CastExpressionSyntax castExpr)
            {
                var targetType = context.SemanticModel.GetTypeInfo(castExpr.Type).Type;

                EmitExpression(castExpr.Expression, targetReg, context, tempOffset);

                if (targetType != null)
                {
                    if (targetType.SpecialType == SpecialType.System_Byte)
                    {
                        context.Emit($"UXTB r{targetReg}, r{targetReg}");
                        context.Write16((ushort)(0xB2C0 | ((targetReg & 0x7) << 3) | (targetReg & 0x7)));
                    }
                    else if (targetType.SpecialType == SpecialType.System_SByte)
                    {
                        context.Emit($"SXTB r{targetReg}, r{targetReg}");
                        context.Write16((ushort)(0xB240 | ((targetReg & 0x7) << 3) | (targetReg & 0x7)));
                    }
                    else if (targetType.SpecialType == SpecialType.System_UInt16)
                    {
                        context.Emit($"UXTH r{targetReg}, r{targetReg}");
                        context.Write16((ushort)(0xB280 | ((targetReg & 0x7) << 3) | (targetReg & 0x7)));
                    }
                    else if (targetType.SpecialType == SpecialType.System_Int16)
                    {
                        context.Emit($"SXTH r{targetReg}, r{targetReg}");
                        context.Write16((ushort)(0xB200 | ((targetReg & 0x7) << 3) | (targetReg & 0x7)));
                    }
                }
            }
            else if (expr is MemberAccessExpressionSyntax memberAccess)
            {
                // Проверяем, не является ли это константой (например, Enum: GPIO_Port.PortA)
                if (TryGetAsConstant(memberAccess, context, out object constVal))
                {
                    EmitMovImmediate(targetReg, Convert.ToInt32(constVal), context);
                    return;
                }

                var typeInfo = context.SemanticModel.GetTypeInfo(memberAccess.Expression);
                if (typeInfo.Type?.TypeKind == TypeKind.Array && memberAccess.Name.ToString() == "Length")
                {
                    int arrReg = tempOffset + 1;
                    EmitExpression(memberAccess.Expression, arrReg, context, tempOffset);

                    int fourReg = tempOffset + 2;
                    EmitMovImmediate(fourReg, 4, context);

                    int addrReg = tempOffset + 3;
                    EmitArithmeticOp(SyntaxKind.SubtractExpression, addrReg, arrReg, fourReg, context);

                    context.Emit($"LDR r{targetReg}, [r{addrReg}, #0]");
                    return;
                }

                // Иначе это чтение свойства объекта или поля (obj.Property или obj.Field)
                var memberSymbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
                if (memberSymbolInfo is IPropertySymbol propertySymbol)
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
                else if (memberSymbolInfo is IFieldSymbol fieldSymbol)
                {
                    string fieldName = fieldSymbol.Name;
                    string typeName = fieldSymbol.ContainingType.ToDisplayString();

                    // Ищем метаданные типа, чтобы узнать смещение поля.
                    if (context.Class.Global.Childs.TryGetValue(typeName, out var typeCtx) && typeCtx is TypeCompilationContext tcc)
                    {
                        if (tcc.FieldOffsets.TryGetValue(fieldName, out int fieldOffset))
                        {
                            string structName = memberAccess.Expression.ToString();
                            
                            // Проверяем, лежит ли эта переменная на стеке (локальная структура)
                            if (context.StackMap.TryGetValue(structName, out var stackVar))
                            {
                                EmitMemoryAccess(true, targetReg, 13, stackVar.StackOffset + fieldOffset, context);
                            }
                            else
                            {
                                // Иначе считаем, что это объект или структура по ссылке/указателю.
                                // Вычисляем выражение, чтобы получить базовый адрес в регистр.
                                int baseReg = tempOffset + 1;
                                EmitExpression(memberAccess.Expression, baseReg, context, tempOffset + 1);
                                EmitMemoryAccess(true, targetReg, baseReg, fieldOffset, context);
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
                context.MarkLabel(falseLabel);
                EmitExpression(ternary.WhenFalse, targetReg, context, tempOffset);

                // 4. Финал
                context.MarkLabel(endLabel);
            }
            else if (expr is ObjectCreationExpressionSyntax objectCreation)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(objectCreation).Type;
                Console.WriteLine($"[DEBUG-ALLOC] ObjectCreation: {objectCreation}. TypeSymbol: {typeSymbol?.ToDisplayString()}, TypeKind: {typeSymbol?.TypeKind}");

                // Handle Delegate Explicit Creation early
                if (typeSymbol != null && typeSymbol.TypeKind == TypeKind.Delegate)
                {
                    var ctorArg = objectCreation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                    var symInfo = ctorArg != null ? context.SemanticModel.GetSymbolInfo(ctorArg).Symbol : null;
                    var memGrp = ctorArg != null ? context.SemanticModel.GetMemberGroup(ctorArg).FirstOrDefault() : null;
                    Console.WriteLine($"[DEBUG] ctorArg for {objectCreation} is {ctorArg}. symInfo: {symInfo != null}, memGrp: {memGrp != null}");

                    var targetMethodCtor = symInfo as IMethodSymbol ?? memGrp as IMethodSymbol;

                    if (targetMethodCtor != null)
                    {
                        EmitDelegateCreation(targetMethodCtor, ctorArg, typeSymbol, targetReg, context, tempOffset);
                        return;
                    }
                    Console.WriteLine($"[DEBUG] Failed to resolve target method for explicit delegate creation: {objectCreation}");
                }

                int size = 0; // default empty size
                bool hasHeader = false;

                if (typeSymbol != null)
                {
                    hasHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true && typeSymbol.IsReferenceType;
                    if (hasHeader) size += 4;

                    // Compute basic size for classes (just counting fields * 4)
                    int fieldsCount = typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count();
                    size += fieldsCount * 4;
                    if (size == 0) size = 4; // minimum alloc size
                }
                else
                {
                    size = 4;
                }

                EmitMovImmediate(0, size, context);

                // Call the native allocator (returns ptr in R0)
                EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

                // If memory has a header, we fill it with pointer to type metadata
                if (hasHeader)
                {
                    string targetName = typeSymbol.ToDisplayString();
                    string symbolName = context.Class.Global.RegisterTypeLiteral(typeSymbol);
                    context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                    int tmpReg = context.NextFreeRegister++;
                    context.Emit($"@ Write TypeHeader for {targetName}");
                    context.Emit($"LDR r{tmpReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                    context.Bin.Write(new byte[8], 0, 8);

                    context.Emit($"STR r{tmpReg}, [r0, #0]"); // Put type ptr at beginning of allocation
                    context.NextFreeRegister--;
                }

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
                        var argument = args[i];
                        int argReg = i + 1; // regOffset is 1 since r0 is "this"
                        if (argReg > 3) throw new Exception("Поддерживается максимум 4 аргумента (включая this)");

                        if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                        {
                            EmitAddressOf(argument.Expression, argReg, context, tempOffset);
                            continue;
                        }

                        EmitExpression(args[i].Expression, argReg, context, tempOffset);
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

                    // multiply size by 4 bytes (assuming 32 bit structures/refs) and add 4 bytes for length header
                    int fourReg = tempOffset + 3;
                    EmitMovImmediate(calcReg, 4, context);
                    EmitMovImmediate(fourReg, 4, context);
                    EmitArithmeticOp(SyntaxKind.MultiplyExpression, calcReg, lengthReg, calcReg, context);
                    EmitArithmeticOp(SyntaxKind.AddExpression, calcReg, calcReg, fourReg, context); // size = length * 4 + 4

                    if (calcReg != 0) EmitMovRegister(0, calcReg, context);
                    EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

                    int arrPtrReg = targetReg != 0 ? targetReg : tempOffset + 2;
                    if (arrPtrReg != 0) EmitMovRegister(arrPtrReg, 0, context);

                    // Write length at offset 0
                    context.Emit($"STR r{lengthReg}, [r{arrPtrReg}, #0]");
                    // Advance pointer to first element
                    EmitArithmeticOp(SyntaxKind.AddExpression, arrPtrReg, arrPtrReg, fourReg, context);

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

                if (methodSymbol.MethodKind == MethodKind.DelegateInvoke)
                {
                    int delegateReg = context.NextFreeRegister++;
                    EmitExpression(invocation.Expression, delegateReg, context, tempOffset);

                    // Load Target into r0, and MethodPtr into r12 (or temporary register)
                    // We assume Delegate is inherited and has `Target` and `MethodPtr` fields
                    int targetOffset = 0;
                    int ptrOffset = 0;
                    if (context.Class.Global.Childs.TryGetValue("System.Delegate", out var delTypeCtx) && delTypeCtx is TypeCompilationContext delTcc)
                    {
                        delTcc.FieldOffsets.TryGetValue("Target", out targetOffset);
                        delTcc.FieldOffsets.TryGetValue("MethodPtr", out ptrOffset);
                    }
                    else
                    {
                        // Fallback offsets (assuming TypeHeader takes 4 bytes if present, but we don't know). Safe fallback:
                        bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
                        targetOffset = typeHeader ? 4 : 0;
                        ptrOffset = targetOffset + 4;
                    }

                    // Arguments start from r1 for Delegate 'this' wrapper is not sent, rather 'Target' is sent.
                    // Wait, if Target is null, r0 is null, but we just pass it anyway (for static methods r0 is practically ignored or we just send it).
                    // Actually, if it's static, the method doesn't expect 'this' in r0. If we send it, it might overwrite the first argument!
                    // Wait, ARM calling convention: r0-r3 are arguments. 
                    // If target is static, r0 is the first argument! We can't just put Target in r0 blindly.
                    // So we must check if the original bound method was static? We don't know it here at runtime easily without a flag.
                    // Instead, in .NET Micro, we can do a trick: we always pass Target as first argument ONLY if Target != null?
                    // No, static methods take arg1 in r0.
                    // For now, let's keep it simple: we always call through an adapter or we don't support mixed static/instance well yet.
                    // Let's assume instance methods for delegates for now (Target to r0, args to r1-r3).

                    context.NextFreeRegister = delegateReg + 1; // reserve r0-r3 for call

                    // Evaluate arguments first, storing them in stack or higher regs?
                    // Actually, let's just evaluate them carefully.
                    // Since r0 is taken by Target, user args go to r1..3.
                    for (int i = 0; i < args.Count; i++)
                    {
                        int argReg = i + 1;
                        if (argReg > 3) throw new Exception("Поддерживается максимум 3 аргумента для делегатов");
                        EmitExpression(args[i].Expression, argReg, context, tempOffset);
                    }

                    context.Emit($"LDR r0, [r{delegateReg}, #{targetOffset}] @ Load Target");
                    int methodPtrReg = context.NextFreeRegister++;
                    context.Emit($"LDR r{methodPtrReg}, [r{delegateReg}, #{ptrOffset}] @ Load MethodPtr");

                    context.Emit($"BLX r{methodPtrReg} @ Invoke Delegate");
                    // T1 16-bit encoding for BLX Rm -> 010001111_Rm_000
                    context.Write16((ushort)(0x4780 | (methodPtrReg << 3)));

                    context.NextFreeRegister -= 2;

                    if (targetReg != 0)
                    {
                        EmitMovRegister(targetReg, 0, context);
                    }
                    return;
                }

                bool isBaseCall = invocation.Expression is MemberAccessExpressionSyntax m && m.Expression is BaseExpressionSyntax;
                bool isInterfaceCall = !methodSymbol.IsStatic && !isBaseCall && methodSymbol.ContainingType.TypeKind == TypeKind.Interface;
                bool isVirtualCall = !methodSymbol.IsStatic && !isBaseCall && !isInterfaceCall && 
                                     (methodSymbol.IsVirtual || methodSymbol.IsAbstract || methodSymbol.IsOverride);

                int regOffset = 0;
                int instanceReg = 0;
                int interfaceFuncPtrReg = 0;

                if (!methodSymbol.IsStatic)
                {
                    // Allocate a temporary register for the instance pointer so it's not clobbered by arguments
                    instanceReg = context.NextFreeRegister++;

                    if (invocation.Expression is MemberAccessExpressionSyntax invokationMemberAccess)
                        EmitExpression(invokationMemberAccess.Expression, instanceReg, context, tempOffset);
                    else
                        context.Emit($"MOV r{instanceReg}, r4"); // fallback "this"

                    regOffset = 1;

                    if (isInterfaceCall)
                    {
                        var ifaceMethods = methodSymbol.ContainingType.GetMembers().OfType<IMethodSymbol>().ToList();
                        int methodIndex = ifaceMethods.IndexOf(methodSymbol.OriginalDefinition);
                        if (methodIndex < 0) methodIndex = ifaceMethods.IndexOf(methodSymbol);

                        context.Emit($"LDR r0, [r{instanceReg}, #0] @ read TypeMetadata");

                        string symbolName = context.Class.Global.RegisterTypeLiteral(methodSymbol.ContainingType);
                        context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);
                        context.Emit($"LDR r1, ={symbolName} ; (placeholder for MOVW/MOVT)");
                        context.Bin.Write(new byte[8], 0, 8);

                        EmitMovImmediate(2, methodIndex, context);

                        var typeHelperType = context.SemanticModel.Compilation.GetTypeByMetadataName("System.MCU.TypeHelper");
                        var findInterfaceMethod = typeHelperType?.GetMembers("FindInterfaceMethod").OfType<IMethodSymbol>().FirstOrDefault();
                        string helperTarget = findInterfaceMethod != null ? findInterfaceMethod.ToDisplayString() : "System.MCU.TypeHelper.FindInterfaceMethod(System.IntPtr, System.IntPtr, int)";

                        EmitCall(helperTarget, context, isStatic: true, isNative: false);

                        interfaceFuncPtrReg = context.NextFreeRegister++;
                        EmitMovRegister(interfaceFuncPtrReg, 0, context);
                    }
                }
                else
                {
                    context.NextFreeRegister = 4; // В статических методах 'this' нет, начинаем с r4
                }

                // Evaluate arguments into temporary safe registers first
                List<int> argRegs = new();
                for (int i = 0; i < args.Count; i++)
                {
                    var argument = args[i];
                    int safeReg = context.NextFreeRegister++; // allocate safe temp var

                    if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    {
                        EmitAddressOf(argument.Expression, safeReg, context, tempOffset);
                    }
                    else
                    {
                        EmitExpression(argument.Expression, safeReg, context, tempOffset);
                    }
                    argRegs.Add(safeReg);
                }

                // Push stack arguments (5th +) in reverse order AAPCS
                int stackArgsCount = 0;
                for (int i = args.Count - 1; i >= 0; i--)
                {
                    int targetArgReg = i + regOffset;
                    if (targetArgReg > 3)
                    {
                        // Needs to go on stack
                        context.Emit($"PUSH {{r{argRegs[i]}}} @ stack argument {i}");
                        // T1 PUSH: 1011_0100_register_list -> 0xB400 | (1 << Reg)
                        context.Write16((ushort)(0xB400 | (1 << argRegs[i])));
                        stackArgsCount++;
                    }
                }

                // Move evaluated arguments into AAPCS registers (R1-R3 for instance methods, R0-R3 for static)
                for (int i = 0; i < args.Count; i++)
                {
                    int targetArgReg = i + regOffset;
                    if (targetArgReg <= 3)
                    {
                        if (argRegs[i] != targetArgReg)
                        {
                            EmitMovRegister(targetArgReg, argRegs[i], context);
                        }
                    }
                }

                // Move instance pointer to R0
                if (!methodSymbol.IsStatic)
                {
                    if (instanceReg != 0)
                        EmitMovRegister(0, instanceReg, context);
                }

                // Cleanup temps
                if (!methodSymbol.IsStatic) context.NextFreeRegister--;
                context.NextFreeRegister -= args.Count;

                var nativeAttr = methodSymbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true);
                string nativeFunctionName = nativeAttr?.ConstructorArguments.FirstOrDefault().Value?.ToString();
                string callTarget = nativeFunctionName ?? methodSymbol.ToDisplayString();

                if (isInterfaceCall)
                {
                    context.Emit($"BLX r{interfaceFuncPtrReg} @ call Interface Method");
                    // T1 16-bit encoding for BLX Rm -> 010001111_Rm_000
                    context.Write16((ushort)(0x4780 | (interfaceFuncPtrReg << 3)));
                    context.NextFreeRegister--; // interfaceFuncPtrReg
                }
                else if (isVirtualCall && nativeAttr == null)
                {
                    int vtableIndex = VTableBuilder.GetVTableIndex(methodSymbol);
                    if (vtableIndex < 0) throw new Exception($"VTable index not found for virtual method {methodSymbol}");

                    int typeMetaReg = context.NextFreeRegister++;
                    context.Emit($"LDR r{typeMetaReg}, [r0, #0] @ read TypeMetadata");

                    int funcPtrReg = context.NextFreeRegister++;
                    int offset = 12 + vtableIndex * 4;
                    context.Emit($"LDR r{funcPtrReg}, [r{typeMetaReg}, #{offset}] @ read Virtual Method");

                    context.Emit($"BLX r{funcPtrReg} @ call Virtual Method");
                    // T1 16-bit encoding for BLX Rm -> 010001111_Rm_000
                    context.Write16((ushort)(0x4780 | (funcPtrReg << 3)));

                    context.NextFreeRegister -= 2;
                }
                else if (methodSymbol.ContainingType.Name == "Unsafe" && (methodSymbol.Name == "AsPointer" || methodSymbol.Name == "As"))
                {
                    // Intrinsic: AsPointer/As just returns its first argument (already in r0)
                    context.Emit($"@ Intrinsic: Unsafe.{methodSymbol.Name} (no-op)");
                }
                else
                {
                    EmitCall(callTarget, context, methodSymbol.IsStatic, nativeAttr != null);
                }

                // Если мы push'или аргументы, надо очистить их со стека
                if (stackArgsCount > 0)
                {
                    context.Emit($"ADD SP, SP, #{stackArgsCount * 4} @ Cleanup stack arguments");
                    uint imm12 = (uint)(stackArgsCount * 4);
                    // ADD SP, SP, #imm (T3)
                    uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                    context.Write32(op);
                }

                // Результат в R0 -> переносим по месту назначения
                if (targetReg != 0)
                {
                    EmitMovRegister(targetReg, 0, context);
                }
            }
        }

        public static void EmitMethodPrologue(bool isInstance, System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters, MethodCompilationContext context)
        {
            // Сохраняем регистры, которые мы будем использовать.
            context.Emit("PUSH {r4-r11, lr}");
            context.Write16(0xB5F0); // PUSH {r4-r7, lr} - нужно будет расширить до r11

            if (context.StackSize > 0)
            {
                int size = (context.StackSize + 7) & ~7;
                context.Emit($"SUB SP, SP, #{size}");
                uint imm12 = (uint)size;
                uint op = 0xF1AD0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                context.Write32(op);
            }

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
                int destReg = context.NextFreeRegister++;
                string paramName = parameters[i].Name;
                if (context.NextFreeRegister > 11)
                    throw new Exception("Недостаточно регистров для сохранения всех аргументов метода.");

                if (sourceReg <= 3)
                {
                    // Registers r0-r3
                    EmitMovRegister(destReg, sourceReg, context);
                    context.Emit($"@ Save parameter '{paramName}' from r{sourceReg} to r{destReg}");
                }
                else
                {
                    // Arguments 5+ are on the stack.
                    // The stack layout looks like this at method start:
                    // SP + 0: Pushed r4-r11, lr (which is 9 registers = 36 bytes)
                    // SP + 36: Local Variables (StackSize)
                    // SP + 36 + StackSize: Here is the 5th argument!
                    // Wait, context.StackSize is already subtracted from SP.
                    // The original SP before our prologue was higher.
                    // So SP + context.StackSize + (9 * 4) + ((sourceReg - 4) * 4) is the address of the argument.

                    int stackArgsOffset = context.StackSize + (9 * 4) + ((sourceReg - 4) * 4);
                    context.Emit($"LDR r{destReg}, [SP, #{stackArgsOffset}] @ Load stack parameter '{paramName}'");

                    // LDR Rt, [SP, #imm]
                    uint op = 0xF8D00000;
                    op |= 13 << 16; // base = SP
                    op |= ((uint)destReg & 0xF) << 12;
                    op |= (uint)stackArgsOffset & 0xFFF;
                    context.Write32(op);
                }

                // "Фиксируем" регистр за параметром
                context.RegisterMap[paramName] = destReg;
            }
        }
        public static void EmitMethodEpilogue(MethodCompilationContext context)
        {
            context.MarkLabel($"{context.Name}_exit"); // Метка для быстрых выходов (return)

            if (context.StackSize > 0)
            {
                int size = (context.StackSize + 7) & ~7;
                context.Emit($"ADD SP, SP, #{size}");
                uint imm12 = (uint)size;
                uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                context.Write32(op);
            }

            context.Emit("POP {r4-r11, pc}");

            // Бинарный код для POP {r4-r11, pc} 
            // r4-r11 (8 регистров) + PC = 9 бит в маске
            context.Write16(0xBDF0);

            context.Emit(".align 4"); // Выравнивание для следующей функции
        }

        public static void ResolveJumps(MethodCompilationContext context)
        {
            byte[] binary = context.Bin.ToArray();
            foreach (var jump in context.Jumps)
            {
                if (!context.Labels.TryGetValue(jump.TargetLabel, out int targetOffset))
                    throw new Exception($"Не найдена метка {jump.TargetLabel}");

                // Адрес от которого считается относительный переход
                int instructionOffset = jump.Offset;
                // PC в ARM Thumb обычно на 4 байта впереди текущей инструкции
                int relativeOffset = targetOffset - instructionOffset - 4; 

                if (jump.IsConditional)
                {
                    // Относительный переход для B<cc> должен быть в пределах -256..254 байт (8 бит)
                    int imm8 = relativeOffset / 2;
                    binary[instructionOffset] = (byte)(imm8 & 0xFF);
                }
                else
                {
                    // Безусловный переход B (16-bit)
                    int imm11 = relativeOffset / 2;
                    binary[instructionOffset] = (byte)(imm11 & 0xFF);
                    binary[instructionOffset + 1] = (byte)(0xE0 | ((imm11 >> 8) & 0x7));
                }
            }

            // Переписываем бинарник с пропатченными адресами
            context.Bin.Position = 0;
            context.Bin.Write(binary, 0, binary.Length);
        }

        public static int ParseLiteral(LiteralExpressionSyntax literal, MethodCompilationContext context)
        {
            if (literal.Token.ValueText == "true") return 1;
            if (literal.Token.ValueText == "false") return 0;
            if (literal.Token.Value == null) return 0;

            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (context != null)
                {
                    var strValue = literal.Token.ValueText;
                    var label = context.Class.Global.RegisterStringLiteral(strValue);
                    // This is handled via rels usually, return 0 for now as it needs a relocation
                    return 0;
                }
                return 0;
            }

            try
            {
                return Convert.ToInt32(literal.Token.Value);
            }
            catch
            {
                return 0;
            }
        }

        public static void EmitCompare(int left, int right, MethodCompilationContext context)
        {
            context.Emit($"CMP r{left}, r{right}");
            // CMP Rn, Rm (Thumb-16: 0x4280 | Rm << 3 | Rn
            context.Write16((ushort)(0x4280 | ((right & 0x7) << 3) | (left & 0x7)));
        }

        public static void EmitCompareImmediate(int reg, int imm, MethodCompilationContext context)
        {
            context.Emit($"CMP r{reg}, #{imm}");
            // CMP Rn, #imm (Thumb-16: 0x2800 | Rn << 8 | imm8)
            context.Write16((ushort)(0x2800 | ((reg & 0x7) << 8) | (imm & 0xFF)));
        }

        public static void EmitBranch(string label, string condition, MethodCompilationContext context)
        {
            context.Emit($"B{condition} {label}");

            byte condCode = condition switch
            {
                "EQ" => 0,
                "NE" => 1,
                "GE" => 10,
                "LT" => 11,
                "GT" => 12,
                "LE" => 13,
                _ => throw new Exception("Unknown branch cond")
            };

            context.AddJump(label, true);
            context.Bytecode(0x00); // placeholder
            context.Bytecode((byte)(0xD0 | condCode));
        }

        public static void EmitJump(string label, MethodCompilationContext context)
        {
            context.Emit($"B {label}");
            context.AddJump(label, false);
            context.Bytecode(0x00); // placeholder
            context.Bytecode(0xE0);
        }

        public static void EmitCall(string name, MethodCompilationContext context, bool isStatic, bool isNative = false)
        {
            context.Emit($"BL {name}");
            context.AddRelocation(name, isStatic, isNative);
            // Thumb-2 BL instruction takes 4 bytes (two 16-bit halfwords)
            // Placeholder: 0xF000 0xD000 (which is basically a branch to self or 0 offset, will be patched later)
            context.Write16((ushort)0xF000);
            context.Write16((ushort)0xD000);
        }

        public static void EmitReturn(MethodCompilationContext context)
        {
            context.Emit("BX LR"); // Возврат из функции (переключение на LR)
        }

        public static void EmitNop(MethodCompilationContext context)
        {
            context.Emit("NOP");
            context.Write16(0x46C0); // NOP для Thumb-16
        }
        public static void EmitBreakpoint(MethodCompilationContext context)
        {
            context.Emit("BKPT #0");
            context.Write16(0xBE00); // BKPT для Thumb-16
        }

        public static int GetStackPointerOffset(MethodCompilationContext context)
        {
            // thumb-16: r13 (SP) всегда указывает на вершину стека, и мы выделяем/освобождаем места относительно него.
            return 13;
        }

        public static void EmitAllocateStack(MethodCompilationContext context, int size)
        {
            // Выделение места в стеке (уменьшение SP)
            context.Emit($"SUB SP, SP, #{size}");

            uint imm12 = (uint)size;
            uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
            context.Write32(op);
        }

        public static void EmitReleaseStack(MethodCompilationContext context, int size)
        {
            // Освобождение места в стеке (увеличение SP)
            context.Emit($"ADD SP, SP, #{size}");

            uint imm12 = (uint)size;
            uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
            context.Write32(op);
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
            uint rd = binary[offset]; // Мы сохранили 1 байт targetReg в плейсхолдер

            uint lower = value & 0xFFFF;
            uint movwHigh = 0xF240 | (((lower >> 11) & 0x1) << 10) | ((lower >> 12) & 0xF);
            uint movwLow  = (((lower >> 8) & 0x7) << 12) | (rd << 8) | (lower & 0xFF);

            uint upper = (value >> 16) & 0xFFFF;
            uint movtHigh = 0xF2C0 | (((upper >> 11) & 0x1) << 10) | ((upper >> 12) & 0xF);
            uint movtLow  = (((upper >> 8) & 0x7) << 12) | (rd << 8) | (upper & 0xFF);

            // Записываем инструкции: сначала старшее полуслово, затем младшее
            binary[offset + 0] = (byte)(movwHigh & 0xFF);
            binary[offset + 1] = (byte)(movwHigh >> 8);
            binary[offset + 2] = (byte)(movwLow & 0xFF);
            binary[offset + 3] = (byte)(movwLow >> 8);

            binary[offset + 4] = (byte)(movtHigh & 0xFF);
            binary[offset + 5] = (byte)(movtHigh >> 8);
            binary[offset + 6] = (byte)(movtLow & 0xFF);
            binary[offset + 7] = (byte)(movtLow >> 8);
        }
    }
}
