using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace NETMCUCompiler.CodeBuilder.Backends
{
    public class CortexM4Backend : MCUBackend
    {
        public override TypeCompilationContext CreateTypeContext(TypeDeclarationSyntax type, SemanticModel semanticModel, CompilationContext global, string name)
        {
            return new TypeCompilationContext(type, semanticModel, global) { Name = name };
        }

        public override MethodCompilationContext CreateMethodContext(SyntaxNode methodSyntax, IMethodSymbol symbol, BaseCompilationContext parentContext, string name)
        {
            return new MethodCompilationContext(methodSyntax, symbol, parentContext) { Name = name };
        }

        public override void GenerateMethodPrologue(MethodCompilationContext context, bool isInstance, ImmutableArray<IParameterSymbol> parameters)
        {
            // Сохраняем регистры, которые мы будем использовать.
            context.Emit("PUSH {r4-r11, lr}");
            context.Write16(0xB5F0); // PUSH {r4-r7, lr}

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
                    EmitMovRegister(context, destReg, sourceReg);
                    context.Emit($"@ Save parameter '{paramName}' from r{sourceReg} to r{destReg}");
                }
                else
                {
                    // Arguments 5+ are on the stack.
                    int stackArgOffset = (sourceReg - 4) * 4; 
                    int loadOffset = stackArgOffset + 36 + ((context.StackSize + 7) & ~7);

                    context.Emit($"LDR r{destReg}, [SP, #{loadOffset}] @ Load param '{paramName}'");
                    uint imm12 = (uint)loadOffset;
                    uint ldrOp = 0xF8DDF000 | (uint)(destReg << 12) | imm12;
                    context.Write32(ldrOp);
                }

                context.RegisterMap[paramName] = destReg;
            }
        }

        public override void GenerateMethodEpilogue(MethodCompilationContext context)
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
            context.Write16(0xBDF0);

            context.Emit(".align 4"); // Выравнивание для следующей функции
        }

        public override void ResolveJumps(MethodCompilationContext context)
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

                if (jump.Type == JumpType.Conditional)
                {
                    // Относительный переход для B<cc> должен быть в пределах -256..254 байт (8 бит)
                    int imm8 = relativeOffset / 2;
                    binary[instructionOffset] = (byte)(imm8 & 0xFF);
                }
                else if (jump.Type == JumpType.LoadAddress)
                {
                    if (relativeOffset < 0) throw new Exception("Backwards ADR not supported yet");
                    
                    int rd = 0; // We assume Rd=r0 based on TryStatement usage
                    uint imm12 = (uint)relativeOffset;
                    uint i = (imm12 >> 11) & 1;
                    uint imm3 = (imm12 >> 8) & 7;
                    uint imm8 = imm12 & 0xFF;

                    uint op = 0xF2AF0000;
                    op |= (i << 26);
                    op |= (imm3 << 12);
                    op |= ((uint)rd << 8);
                    op |= imm8;

                    ushort high = (ushort)(op >> 16);
                    ushort low = (ushort)(op & 0xFFFF);
                    
                    binary[instructionOffset + 0] = (byte)(high & 0xFF);
                    binary[instructionOffset + 1] = (byte)(high >> 8);
                    binary[instructionOffset + 2] = (byte)(low & 0xFF);
                    binary[instructionOffset + 3] = (byte)(low >> 8);
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

        public void PatchThumb2BL(byte[] binary, int offset, int jumpOffset)
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

        public void PatchMovwMovt(byte[] binary, int offset, uint value)
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

        public override void PatchCall(byte[] binary, int offset, int jumpOffset) => PatchThumb2BL(binary, offset, jumpOffset);
        public override void PatchDataAddress(byte[] binary, int offset, uint value) => PatchMovwMovt(binary, offset, value);

        public override void GenerateTryStatement(MethodCompilationContext context, Action generateTryBlock, Action<CatchClauseSyntax> generateCatchBlock, Action generateFinallyBlock, SyntaxList<CatchClauseSyntax> catches, FinallyClauseSyntax finallyClause)
        {
            context.Emit("@ TRY BLOCK START");

            string catchLabel = context.NextLabel("CATCH_HANDLER");
            string endLabel = context.NextLabel("TRY_END");

            // Помещаем адрес обработчика в r0 и вызываем NETMCU_TryPush
            context.Emit($"ADR.W r0, {catchLabel}");
            context.AddLoadAddress(catchLabel);
            context.Write32(0xF2AF0000); // Placeholder for ADR.W r0, label

            EmitCall(context, "NETMCU_TryPush", isStatic: true, isNative: true);

            generateTryBlock();

            // Если блок выполнился успешно без throw, снимаем обработчик
            EmitCall(context, "NETMCU_TryPop", isStatic: true, isNative: true);
            context.Emit($"B {endLabel}");
            context.AddJump(endLabel, false);
            context.Write16(0xE000); // branch empty

            // Сам функция-обработчик catch (вызывается из throw)
            context.MarkLabel(catchLabel);

            // Снимаем себя из стека обработчиков, чтобы следующий throw полетел выше
            EmitCall(context, "NETMCU_TryPop", isStatic: true, isNative: true); 

            // Обрабатываем блоки catch
            foreach (var catchClause in catches)
            {
                context.Emit($"@ CATCH BLOCK: {catchClause.Declaration?.Type?.ToString()}");
                generateCatchBlock(catchClause);
            }

            // Возврат в throw! (как и просил пользователь, без сложной раскрутки)
            context.Emit($"B {endLabel}");
            context.AddJump(endLabel, false);
            context.Write16(0xE000); // branch empty

            context.MarkLabel(endLabel);

            if (finallyClause != null)
            {
                context.Emit("@ FINALLY BLOCK");
                generateFinallyBlock();
            }

            context.Emit("@ TRY BLOCK END");
        }

        public override void EmitVariableAllocation(MethodCompilationContext context, VariableAllocationContext allocContext)
        {
            if (allocContext.IsStack)
            {
                context.Emit($"@ Allocation: {allocContext.VarName} -> Stack[{allocContext.StackOffset}]");
                if (allocContext.HasInitializer)
                {
                    EmitMemoryAccess(context, false, allocContext.InitValueReg, 13, allocContext.StackOffset);
                }
            }
            else
            {
                context.Emit($"@ Allocation: {allocContext.VarName} -> r{allocContext.RegisterIndex}");
            }
        }

        public override void EmitStoreToArrayElement(MethodCompilationContext context, int elementSize, int valueReg, int destAddrReg)
        {
            if (elementSize == 1)
            {
                context.Emit($"STRB r{valueReg}, [r{destAddrReg}, #0]");
                context.Write16((ushort)(0x7000 | ((destAddrReg & 0x7) << 3) | (valueReg & 0x7)));
            }
            else if (elementSize == 2)
            {
                context.Emit($"STRH r{valueReg}, [r{destAddrReg}, #0]");
                context.Write16((ushort)(0x8000 | ((destAddrReg & 0x7) << 3) | (valueReg & 0x7)));
            }
            else
            {
                context.Emit($"STR r{valueReg}, [r{destAddrReg}, #0]");
                context.Write16((ushort)(0x6000 | ((destAddrReg & 0x7) << 3) | (valueReg & 0x7)));
            }
        }

        public override void EmitLoadFromArrayElement(MethodCompilationContext context, int elementSize, int resultReg, int sourceAddrReg)
        {
            if (elementSize == 1)
            {
                context.Emit($"LDRB r{resultReg}, [r{sourceAddrReg}, #0]");
                context.Write16((ushort)(0x7800 | ((sourceAddrReg & 0x7) << 3) | (resultReg & 0x7)));
            }
            else if (elementSize == 2)
            {
                context.Emit($"LDRH r{resultReg}, [r{sourceAddrReg}, #0]");
                context.Write16((ushort)(0x8800 | ((sourceAddrReg & 0x7) << 3) | (resultReg & 0x7)));
            }
            else
            {
                context.Emit($"LDR r{resultReg}, [r{sourceAddrReg}, #0]");
                context.Write16((ushort)(0x6800 | ((sourceAddrReg & 0x7) << 3) | (resultReg & 0x7)));
            }
        }

        public override void EmitJump(MethodCompilationContext context, string label)
        {
            context.Emit($"B {label}");
            context.AddJump(label, false);
            context.Bytecode(0x00); // placeholder
            context.Bytecode(0xE0);
        }

        public override void EmitExpressionValue(MethodCompilationContext context, ExpressionSyntax expr, int targetReg) => ASMInstructions.EmitExpression(expr, targetReg, context);

        public override void EmitCall(MethodCompilationContext context, string name, bool isStatic, bool isNative = false)
        {
            context.Emit($"BL {name}");
            context.AddRelocation(name, isStatic, isNative);
            context.Write16((ushort)0xF000);
            context.Write16((ushort)0xD000);
        }

        public override void EmitMovImmediate(MethodCompilationContext context, int reg, int val)
        {
            if (val <= 255 && val >= 0)
            {
                context.Emit($"MOV r{reg}, #{val}");
                context.Write16((ushort)(0x2000 | (reg << 8) | (val & 0xFF)));
            }
            else
            {
                context.Emit($"MOVW r{reg}, #{val}");
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

        public override void EmitCompare(MethodCompilationContext context, int left, int right)
        {
            context.Emit($"CMP r{left}, r{right}");
            context.Write16((ushort)(0x4280 | ((right & 0x7) << 3) | (left & 0x7)));
        }

        public override void EmitBranch(MethodCompilationContext context, string label, string condition)
        {
            context.Emit($"B{condition} {label}");

            byte condCode = condition.ToUpperInvariant() switch
            {
                "EQ" => 0,
                "NE" => 1,
                "CS" => 2,
                "HS" => 2,
                "CC" => 3,
                "LO" => 3,
                "MI" => 4,
                "PL" => 5,
                "VS" => 6,
                "VC" => 7,
                "HI" => 8,
                "LS" => 9,
                "GE" => 10,
                "LT" => 11,
                "GT" => 12,
                "LE" => 13,
                "AL" => 14,
                _ => throw new Exception($"Unknown branch cond: {condition}")
            };

            context.AddJump(label, true);
            context.Bytecode(0x00);
            context.Bytecode((byte)(0xD0 | condCode));
        }

        public override void EmitMovRegister(MethodCompilationContext context, int target, int source)
        {
            context.Emit($"MOV r{target}, r{source}");
            ushort opcode = 0x4600;
            if (target > 7) opcode |= 0x0080;
            if (source > 7) opcode |= 0x0040;
            opcode |= (ushort)(((source & 0x7) << 3) | (target & 0x7));
            context.Write16(opcode);
        }

        public override void EmitMemoryAccess(MethodCompilationContext context, bool isLoad, int targetReg, int baseReg, int offset)
        {
            if (baseReg == 13) // SP
            {
                context.Emit($"{(isLoad ? "LDR" : "STR")} r{targetReg}, [SP, #{offset}]");
            }
            else
            {
                context.Emit($"{(isLoad ? "LDR" : "STR")} r{targetReg}, [r{baseReg}, #{offset}]");
            }

            uint op = isLoad ? 0xF8D00000u : 0xF8C00000u;
            op |= ((uint)baseReg & 0xF) << 16;
            op |= ((uint)targetReg & 0xF) << 12;
            op |= ((uint)offset & 0xFFF);

            context.Write32(op);
        }

        public override void EmitArithmeticOp(MethodCompilationContext context, SyntaxKind op, int target, int left, int right)
        {
            if (op == SyntaxKind.MultiplyExpression)
            {
                if (target != left) EmitMovRegister(context, target, left);
                context.Emit($"MULS r{target}, r{right}");
                context.Write16((ushort)(0x4340 | ((right & 0x7) << 3) | (target & 0x7)));
                return;
            }

            if (op == SyntaxKind.DivideExpression)
            {
                context.Emit($"SDIV r{target}, r{left}, r{right}");
                uint wideOp = 0xFB90F0F0 | (uint)((left & 0xF) << 16) | (uint)((target & 0xF) << 8) | (uint)(right & 0xF);
                context.Write32(wideOp);
                return;
            }

            bool isAccumulative = op == SyntaxKind.BitwiseAndExpression ||
                                 op == SyntaxKind.BitwiseOrExpression ||
                                 op == SyntaxKind.ExclusiveOrExpression ||
                                 op == SyntaxKind.LeftShiftExpression ||
                                 op == SyntaxKind.RightShiftExpression;

            if (isAccumulative)
            {
                if (target != left) EmitMovRegister(context, target, left);

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

        public override void EmitOpWithImmediate(MethodCompilationContext context, SyntaxKind op, int target, int left, int value)
        {
            string opName = op == SyntaxKind.AddExpression ? "ADDS" : "SUBS";
            context.Emit($"{opName} r{target}, r{left}, #{value}");

            ushort baseOp = (ushort)(op == SyntaxKind.AddExpression ? 0x1C00 : 0x1E00);
            ushort opcode = (ushort)(baseOp | ((value & 0x7) << 6) | (left << 3) | target);

            context.Bytecode((byte)(opcode & 0xFF));
            context.Bytecode((byte)(opcode >> 8));
        }

        public override void EmitAddSP(MethodCompilationContext context, int targetReg, int offset)
        {
            context.Emit($"ADD r{targetReg}, SP, #{offset}");
            
            // T2 ADD.W: 1111_0i10_0000_1101_0_imm3_Rd_imm8
            uint op = 0xF20D0000u;
            op |= ((uint)targetReg & 0xF) << 8;
            uint i = ((uint)offset >> 11) & 1;
            uint imm3 = ((uint)offset >> 8) & 7;
            uint imm8 = (uint)offset & 0xFF;
            op |= (i << 26) | (imm3 << 12) | imm8;
            context.Write32(op);
        }

        public override void EmitCompareImmediate(MethodCompilationContext context, int reg, int imm)
        {
            context.Emit($"CMP r{reg}, #{imm}");
            context.Write16((ushort)(0x2800 | ((reg & 0x7) << 8) | (imm & 0xFF)));
        }
    }
}