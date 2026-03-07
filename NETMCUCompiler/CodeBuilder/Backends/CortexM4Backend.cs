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
            ASMInstructions.EmitMethodPrologue(isInstance, parameters, context);
        }

        public override void GenerateMethodEpilogue(MethodCompilationContext context)
        {
            ASMInstructions.EmitMethodEpilogue(context);
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

            ASMInstructions.EmitCall("NETMCU_TryPush", context, isStatic: true, isNative: true);

            generateTryBlock();

            // Если блок выполнился успешно без throw, снимаем обработчик
            ASMInstructions.EmitCall("NETMCU_TryPop", context, isStatic: true, isNative: true);
            context.Emit($"B {endLabel}");
            context.AddJump(endLabel, false);
            context.Write16(0xE000); // branch empty

            // Сам функция-обработчик catch (вызывается из throw)
            context.MarkLabel(catchLabel);

            // Снимаем себя из стека обработчиков, чтобы следующий throw полетел выше
            ASMInstructions.EmitCall("NETMCU_TryPop", context, isStatic: true, isNative: true); 

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
                    ASMInstructions.EmitMemoryAccess(false, allocContext.InitValueReg, 13, allocContext.StackOffset, context);
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

        public override void EmitJump(MethodCompilationContext context, string label) => ASMInstructions.EmitJump(label, context);
        public override void EmitLogicalCondition(MethodCompilationContext context, ExpressionSyntax condition, string trueLabel, string falseLabel) => ASMInstructions.EmitLogicalCondition(condition, trueLabel, falseLabel, context);
        public override void EmitExpressionValue(MethodCompilationContext context, ExpressionSyntax expr, int targetReg) => ASMInstructions.EmitExpression(expr, targetReg, context);
        public override void EmitCall(MethodCompilationContext context, string name, bool isStatic, bool isNative = false) => ASMInstructions.EmitCall(name, context, isStatic, isNative);
        public override void EmitMovImmediate(MethodCompilationContext context, int reg, int val) => ASMInstructions.EmitMovImmediate(reg, val, context);
        public override void EmitCompare(MethodCompilationContext context, int left, int right) => ASMInstructions.EmitCompare(left, right, context);
        public override void EmitBranch(MethodCompilationContext context, string label, string condition) => ASMInstructions.EmitBranch(label, condition, context);
        public override void EmitMovRegister(MethodCompilationContext context, int target, int source) => ASMInstructions.EmitMovRegister(target, source, context);
        public override void EmitMemoryAccess(MethodCompilationContext context, bool isLoad, int targetReg, int baseReg, int offset) => ASMInstructions.EmitMemoryAccess(isLoad, targetReg, baseReg, offset, context);
        public override void EmitArithmeticOp(MethodCompilationContext context, SyntaxKind op, int target, int left, int right) => ASMInstructions.EmitArithmeticOp(op, target, left, right, context);
        public override void EmitOpWithImmediate(MethodCompilationContext context, SyntaxKind op, int target, int left, int value) => ASMInstructions.EmitOpWithImmediate(op, target, left, value, context);
        public override void EmitAddressOf(MethodCompilationContext context, ExpressionSyntax expr, int targetReg, int tempOffset = 0) => ASMInstructions.EmitAddressOf(expr, targetReg, context, tempOffset);
        public override void EmitCompareImmediate(MethodCompilationContext context, int reg, int imm) => ASMInstructions.EmitCompareImmediate(reg, imm, context);
    }
}