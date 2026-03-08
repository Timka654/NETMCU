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

        public override void EmitComment(MethodCompilationContext context, string comment)
        {
            context.Emit($"@ {comment}");
        }

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
                if (allocContext.HasInitializer)
                {
                    EmitMemoryAccess(context, false, allocContext.InitValueReg, 13, allocContext.StackOffset);
                }
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

        // Base class now handles EmitExpressionValue
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

        public override void EmitLoadSymbolAddress(MethodCompilationContext context, int targetReg, string symbolName)
        {
            context.Emit($"LDR r{targetReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
            context.Bytecode((byte)targetReg);
            context.Bin.Write(new byte[7], 0, 7);
        }

        public override void EmitPush(MethodCompilationContext context, int reg)
        {
            context.Emit($"PUSH {{r{reg}}}");
            context.Write16((ushort)(0xB400 | (1 << reg)));
        }

        public override void EmitAdjustSP(MethodCompilationContext context, int offset)
        {
            if (offset > 0)
            {
                context.Emit($"ADD SP, SP, #{offset}");
                uint imm12 = (uint)offset;
                uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                context.Write32(op);
            }
        }

        public override void EmitCallRegister(MethodCompilationContext context, int reg)
        {
            context.Emit($"BLX r{reg}");
            context.Write16((ushort)(0x4780 | (reg << 3)));
        }
    
        public override void EmitObjectCreation(MethodCompilationContext context, ObjectCreationInfo info, int tempOffset)
        {
            var typeSymbol = info.TypeSymbol;
            int size = 4;
            bool hasHeader = info.HasTypeHeader;
            
            if (typeSymbol != null)
            {
                if (hasHeader) size = 4; else size = 0;
                int fieldsCount = typeSymbol.GetMembers().OfType<Microsoft.CodeAnalysis.IFieldSymbol>().Where(f => !f.IsStatic).Count();
                size += fieldsCount * 4;
                if (size == 0) size = 4; 
            }

            EmitMovImmediate(context, 0, size);
            EmitCall(context, "NETMCU__Memory__Alloc", isStatic: true, isNative: true);

            if (hasHeader)
            {
                context.Class.Global.AddDataRelocation(context, info.SymbolName, (int)context.Bin.Length);
                int tmpReg = context.NextFreeRegister++;
                EmitLoadSymbolAddress(context, tmpReg, info.SymbolName);
                context.Emit($"STR r{tmpReg}, [r0, #0]");
                context.Write16((ushort)(0x6000 | ((0 & 0x7) << 3) | (tmpReg & 0x7)));
                context.NextFreeRegister--;
            }

            if (info.TargetReg != 0) EmitMovRegister(context, info.TargetReg, 0);

            var ctorSymbol = info.CtorSymbol;
            var args = info.Arguments;

            if (ctorSymbol != null && !ctorSymbol.IsImplicitlyDeclared && args != null)
            {
                System.Collections.Generic.List<int> argRegs = new();
                for (int i = 0; i < args.Count; i++)
                {
                    var argument = args[i];
                    int safeReg = context.NextFreeRegister++;
                    if (argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword))
                        EmitAddressOf(context, argument.Expression, safeReg, tempOffset);
                    else
                        EmitExpressionValue(context, argument.Expression, safeReg, tempOffset);
                    argRegs.Add(safeReg);
                }

                int stackArgsCount = 0;
                for (int i = args.Count - 1; i >= 0; i--)
                {
                    int targetArgReg = i + 1;
                    if (targetArgReg > 3)
                    {
                        EmitPush(context, argRegs[i]);
                        stackArgsCount++;
                    }
                }

                if (info.TargetReg != 0) EmitMovRegister(context, 0, info.TargetReg);

                for (int i = 0; i < args.Count; i++)
                {
                    int targetArgReg = i + 1;
                    if (targetArgReg <= 3)
                        if (argRegs[i] != targetArgReg) EmitMovRegister(context, targetArgReg, argRegs[i]);
                }

                EmitCall(context, ctorSymbol.ToDisplayString(), isStatic: false, isNative: false);

                context.NextFreeRegister -= args.Count;

                if (stackArgsCount > 0)
                {
                    EmitAdjustSP(context, stackArgsCount * 4);
                }

                if (info.TargetReg != 0) EmitMovRegister(context, info.TargetReg, 0); // restoring allocated ptr
            }
        }

        public override void EmitArrayCreation(MethodCompilationContext context, ArrayCreationInfo info, int tempOffset)
        {
            int lenReg = context.NextFreeRegister++;
            if (info.SizeExpression != null && !(info.SizeExpression is Microsoft.CodeAnalysis.CSharp.Syntax.OmittedArraySizeExpressionSyntax)) {
                EmitExpressionValue(context, info.SizeExpression, lenReg, tempOffset);
            } else if (info.Initializer != null) {
                EmitMovImmediate(context, lenReg, info.Initializer.Expressions.Count);
            } else {
                EmitMovImmediate(context, lenReg, 0);
            }

            int sizeReg = context.NextFreeRegister++;

            EmitMovImmediate(context, sizeReg, info.ElementSize);
            EmitArithmeticOp(context, Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiplyExpression, sizeReg, lenReg, sizeReg);
            EmitOpWithImmediate(context, Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression, sizeReg, sizeReg, info.HeaderSize);

            EmitMovRegister(context, 0, sizeReg);
            EmitCall(context, "NETMCU__Memory__Alloc", isStatic: true, isNative: true);

            if (info.HasTypeHeader && info.TypeSymbol != null)
            {
                context.Class.Global.AddDataRelocation(context, info.SymbolName, (int)context.Bin.Length);
                int tmpReg = context.NextFreeRegister++;
                EmitLoadSymbolAddress(context, tmpReg, info.SymbolName);
                context.Emit($"STR r{tmpReg}, [r0, #0]");
                context.Write16((ushort)(0x6000 | ((0 & 0x7) << 3) | (tmpReg & 0x7)));
                context.NextFreeRegister--;

                context.Emit($"STR r{lenReg}, [r0, #4] @ Array Length");
                context.Write16((ushort)(0x6040 | ((0 & 0x7) << 3) | (lenReg & 0x7)));
            } 
            else 
            {
                context.Emit($"STR r{lenReg}, [r0, #0] @ Array Length");
                context.Write16((ushort)(0x6000 | ((0 & 0x7) << 3) | (lenReg & 0x7)));
            }

            int arrayAllocReg = info.TargetReg == 0 ? context.NextFreeRegister++ : info.TargetReg;
            if (arrayAllocReg != 0) EmitMovRegister(context, arrayAllocReg, 0);

            if (info.Initializer != null && info.Initializer.Expressions.Count > 0) 
            {
                int valReg = context.NextFreeRegister++;
                int idxReg = context.NextFreeRegister++;
                int ptrReg = context.NextFreeRegister++;
                int iterReg = context.NextFreeRegister++;

                int counter = 0;
                foreach(var valExpr in info.Initializer.Expressions)
                {
                    EmitExpressionValue(context, valExpr, valReg, tempOffset);

                    EmitMovImmediate(context, idxReg, counter);
                    EmitMovImmediate(context, iterReg, info.ElementSize);
                    EmitArithmeticOp(context, Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiplyExpression, idxReg, idxReg, iterReg);
                    EmitOpWithImmediate(context, Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression, idxReg, idxReg, info.HeaderSize);
                    EmitArithmeticOp(context, Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression, ptrReg, arrayAllocReg, idxReg);

                    EmitStoreToArrayElement(context, info.ElementSize, valReg, ptrReg);
                    counter++;
                }

                context.NextFreeRegister -= 4;
            }

            if (info.TargetReg == 0) context.NextFreeRegister--; 
            context.NextFreeRegister -= 2; 
        }

        public override void EmitDelegateCreation(MethodCompilationContext context, Microsoft.CodeAnalysis.IMethodSymbol targetMethod, Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax nodeExpr, Microsoft.CodeAnalysis.ITypeSymbol delegateType, int targetReg, int tempOffset)
        {
            int size = context.Class.Global.BuildingContext.Options?.TypeHeader == true ? 12 : 8; // delegate size is 8 bytes + 4 for header
            EmitMovImmediate(context, 0, size);

            EmitCall(context, "NETMCU__Memory__Alloc", isStatic: true, isNative: true);

            bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
            if (typeHeader)
            {
                string targetName = delegateType.ToDisplayString();
                string symbolName = context.Class.Global.RegisterTypeLiteral(delegateType);
                context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                int tmpReg = context.NextFreeRegister++;
                EmitLoadSymbolAddress(context, tmpReg, symbolName);
                context.Emit($"STR r{tmpReg}, [r0, #0]");
                context.Write16((ushort)(0x6000 | ((0 & 0x7) << 3) | (tmpReg & 0x7)));
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
            if (targetReg != 0) EmitMovRegister(context, delObjReg, 0);

            int methodValReg = context.NextFreeRegister++;
            string methodName = targetMethod.ToDisplayString();
            context.Class.Global.AddRelocation(context, methodName, targetMethod.IsStatic, false, (int)context.Bin.Length);
            EmitLoadSymbolAddress(context, methodValReg, methodName);
            
            context.Emit($"STR r{methodValReg}, [r{delObjReg}, #{ptrOffset}]");
            context.Write16((ushort)(0x6000 | ((ptrOffset/4) << 6) | ((delObjReg & 0x7) << 3) | (methodValReg & 0x7)));

            int targetValReg = context.NextFreeRegister++;
            if (targetMethod.IsStatic)
            {
                EmitMovImmediate(context, targetValReg, 0);
            }
            else
            {
                if (nodeExpr is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax delMemberAccess)
                {
                    EmitExpressionValue(context, delMemberAccess.Expression, targetValReg, tempOffset);
                }
                else
                {
                    context.Emit($"MOV r{targetValReg}, r4");
                    context.Write16((ushort)(0x4600 | (4 << 3) | (targetValReg & 0x7)));
                }
            }
            context.Emit($"STR r{targetValReg}, [r{delObjReg}, #{targetOffset}]");
            context.Write16((ushort)(0x6000 | ((targetOffset/4) << 6) | ((delObjReg & 0x7) << 3) | (targetValReg & 0x7)));

            context.NextFreeRegister -= 2;
        }

    
        public override void EmitInvocation(MethodCompilationContext context, InvocationInfo info, int tempOffset)
        {
            var methodSymbol = info.MethodSymbol;
            if (methodSymbol == null || info.IsDelegateInvoke)
            {
                if (info.IsDelegateInvoke)
                {
                    int delegateReg = context.NextFreeRegister++;
                    var delegateExpression = info.InstanceExpression;
                    if (delegateExpression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Invoke")
                    {
                        delegateExpression = ma.Expression;
                    }

                    EmitExpressionValue(context, delegateExpression, delegateReg, tempOffset);

                    int targetOffset = context.Class.Global.BuildingContext.Options?.TypeHeader == true ? 4 : 0;
                    int ptrOffset = targetOffset + 4;
                    if (context.Class.Global.Childs.TryGetValue("System.Delegate", out var delTypeCtx) && delTypeCtx is TypeCompilationContext delTcc)
                    {
                        delTcc.FieldOffsets.TryGetValue("Target", out targetOffset);
                        delTcc.FieldOffsets.TryGetValue("MethodPtr", out ptrOffset);
                    }

                    var delArgs = info.Arguments;
                    System.Collections.Generic.List<int> delArgRegs = new();
                    if (delArgs != null)
                    {
                        for (int i = 0; i < delArgs.Count; i++)
                        {
                            var argument = delArgs[i];
                            int safeReg = context.NextFreeRegister++;
                            if (argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword))
                                EmitAddressOf(context, argument.Expression, safeReg, tempOffset);
                            else
                                EmitExpressionValue(context, argument.Expression, safeReg, tempOffset);
                            delArgRegs.Add(safeReg);
                        }
                    }

                    int delStackArgsCount = 0;
                    if (delArgs != null)
                    {
                        for (int i = delArgs.Count - 1; i >= 0; i--)
                        {
                            int targetArgReg = i + 1;
                            if (targetArgReg > 3)
                            {
                                EmitPush(context, delArgRegs[i]);
                                delStackArgsCount++;
                            }
                        }

                        for (int i = 0; i < delArgs.Count; i++)
                        {
                            int targetArgReg = i + 1;
                            if (targetArgReg <= 3)
                                if (delArgRegs[i] != targetArgReg) EmitMovRegister(context, targetArgReg, delArgRegs[i]);
                        }
                    }

                    context.Emit($"LDR r0, [r{delegateReg}, #{targetOffset}] @ Load Target");
                    context.Write16((ushort)(0x6800 | ((targetOffset/4) << 6) | ((delegateReg & 0x7) << 3) | (0)));
                    
                    int methodPtrReg = context.NextFreeRegister++;
                    context.Emit($"LDR r{methodPtrReg}, [r{delegateReg}, #{ptrOffset}] @ Load MethodPtr");
                    context.Write16((ushort)(0x6800 | ((ptrOffset/4) << 6) | ((delegateReg & 0x7) << 3) | (methodPtrReg & 0x7)));

                    EmitCallRegister(context, methodPtrReg);

                    context.NextFreeRegister--; // methodPtrReg
                    if (delArgs != null) context.NextFreeRegister -= delArgs.Count;

                    if (delStackArgsCount > 0)
                    {
                        EmitAdjustSP(context, delStackArgsCount * 4);
                    }

                    if (info.TargetReg != 0) EmitMovRegister(context, info.TargetReg, 0);
                }
                return;
            }

            int regOffset = 0;
            int instanceReg = 0;
            int interfaceFuncPtrReg = 0;

            if (!methodSymbol.IsStatic)
            {
                instanceReg = context.NextFreeRegister++;
                if (info.InstanceExpression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax invokationMemberAccess)
                    EmitExpressionValue(context, invokationMemberAccess.Expression, instanceReg, tempOffset);
                else
                {
                    context.Emit($"MOV r{instanceReg}, r4"); 
                    context.Write16((ushort)(0x4600 | (4 << 3) | (instanceReg & 0x7)));
                }

                regOffset = 1;

                if (info.IsInterfaceCall)
                {
                    var ifaceMethods = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<Microsoft.CodeAnalysis.IMethodSymbol>(methodSymbol.ContainingType.GetMembers()));
                    int methodIndex = ifaceMethods.IndexOf(methodSymbol.OriginalDefinition);
                    if (methodIndex < 0) methodIndex = ifaceMethods.IndexOf(methodSymbol);

                    context.Emit($"LDR r0, [r{instanceReg}, #0] @ read TypeMetadata");
                    context.Write16((ushort)(0x6800 | ((instanceReg & 0x7) << 3) | (0)));

                    string symbolName = context.Class.Global.RegisterTypeLiteral(methodSymbol.ContainingType);
                    context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);
                    EmitLoadSymbolAddress(context, 1, symbolName);

                    EmitMovImmediate(context, 2, methodIndex);

                    var typeHelperType = context.SemanticModel.Compilation.GetTypeByMetadataName("System.MCU.TypeHelper");
                    var findInterfaceMethod = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<Microsoft.CodeAnalysis.IMethodSymbol>(typeHelperType?.GetMembers("FindInterfaceMethod") ?? System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.ISymbol>.Empty));
                    string helperTarget = findInterfaceMethod != null ? findInterfaceMethod.ToDisplayString() : "System.MCU.TypeHelper.FindInterfaceMethod(System.IntPtr, System.IntPtr, int)";

                    EmitCall(context, helperTarget, isStatic: true, isNative: false);

                    interfaceFuncPtrReg = context.NextFreeRegister++;
                    EmitMovRegister(context, interfaceFuncPtrReg, 0);
                }
            }

            var args = info.Arguments;
            System.Collections.Generic.List<int> argRegs = new();
            if (args != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    var argument = args[i];
                    int safeReg = context.NextFreeRegister++;

                    if (argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword))
                        EmitAddressOf(context, argument.Expression, safeReg, tempOffset);
                    else
                        EmitExpressionValue(context, argument.Expression, safeReg, tempOffset);
                    argRegs.Add(safeReg);
                }
            }

            int stackArgsCount = 0;
            if (args != null)
            {
                for (int i = args.Count - 1; i >= 0; i--)
                {
                    int targetArgReg = i + regOffset; 
                    if (targetArgReg > 3)
                    {
                        EmitPush(context, argRegs[i]);
                        stackArgsCount++;
                    }
                }
            }

            if (!methodSymbol.IsStatic)
                EmitMovRegister(context, 0, instanceReg); // set 'this'

            if (args != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    int targetArgReg = i + regOffset;
                    if (targetArgReg <= 3)
                        if (argRegs[i] != targetArgReg) EmitMovRegister(context, targetArgReg, argRegs[i]);
                }
            }

            if (info.IsInterfaceCall)
            {
                EmitCallRegister(context, interfaceFuncPtrReg);
                context.NextFreeRegister--; // interfaceFuncPtrReg
            }
            else
            {
                string callTarget = info.NativeFunctionName ?? methodSymbol.ToDisplayString();
                EmitCall(context, callTarget, methodSymbol.IsStatic, info.NativeFunctionName != null);
            }

            if (args != null) context.NextFreeRegister -= args.Count;
            if (!methodSymbol.IsStatic) context.NextFreeRegister--; // instanceReg

            if (stackArgsCount > 0)
            {
                EmitAdjustSP(context, stackArgsCount * 4);
            }

            if (info.TargetReg != 0) EmitMovRegister(context, info.TargetReg, 0);
        }

    }
}
