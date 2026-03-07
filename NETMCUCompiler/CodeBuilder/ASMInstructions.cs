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
        private static void EmitArrayCreation(InitializerExpressionSyntax initializer, ExpressionSyntax sizeExpr, ITypeSymbol typeSymbol, int targetReg, MethodCompilationContext context, int tempOffset)
        {
            int lenReg = context.NextFreeRegister++;
            if (sizeExpr != null && !(sizeExpr is OmittedArraySizeExpressionSyntax)) {
                EmitExpression(sizeExpr, lenReg, context, tempOffset);
            } else if (initializer != null) {
                EmitMovImmediate(lenReg, initializer.Expressions.Count, context);
            } else {
                EmitMovImmediate(lenReg, 0, context);
            }

            int elementSize = 4; // default
            if (typeSymbol is IArrayTypeSymbol arrayType)
            {
                var elType = arrayType.ElementType;
                if (elType.SpecialType == SpecialType.System_Byte || elType.SpecialType == SpecialType.System_SByte || elType.SpecialType == SpecialType.System_Boolean) elementSize = 1;
                else if (elType.SpecialType == SpecialType.System_Int16 || elType.SpecialType == SpecialType.System_UInt16 || elType.SpecialType == SpecialType.System_Char) elementSize = 2;
                else if (elType.SpecialType == SpecialType.System_Int64 || elType.SpecialType == SpecialType.System_UInt64 || elType.SpecialType == SpecialType.System_Double) elementSize = 8;
                else if (elType.TypeKind == TypeKind.Struct)
                    elementSize = Math.Max(1, elType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
            }

            int sizeReg = context.NextFreeRegister++;
            bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
            int headerSize = typeHeader ? 8 : 4;

            EmitMovImmediate(sizeReg, elementSize, context);
            EmitArithmeticOp(SyntaxKind.MultiplyExpression, sizeReg, lenReg, sizeReg, context);
            EmitOpWithImmediate(SyntaxKind.AddExpression, sizeReg, sizeReg, headerSize, context);

            EmitMovRegister(0, sizeReg, context);
            EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

            if (typeSymbol != null && typeHeader)
            {
                string targetName = typeSymbol.ToDisplayString();
                string symbolName = context.Class.Global.RegisterTypeLiteral(typeSymbol);
                context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                int tmpReg = context.NextFreeRegister++;
                context.Emit($"@ Write TypeHeader for array {targetName}");
                context.Emit($"LDR r{tmpReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                context.Bin.Write(new byte[8], 0, 8);
                context.Emit($"STR r{tmpReg}, [r0, #0]");
                context.NextFreeRegister--;

                context.Emit($"STR r{lenReg}, [r0, #4] @ Array Length");
            } 
            else 
            {
                context.Emit($"STR r{lenReg}, [r0, #0] @ Array Length");
            }

            int arrayAllocReg = targetReg == 0 ? context.NextFreeRegister++ : targetReg;
            if (arrayAllocReg != 0) EmitMovRegister(arrayAllocReg, 0, context);

            if (initializer != null && initializer.Expressions.Count > 0) 
            {
                int valReg = context.NextFreeRegister++;
                int idxReg = context.NextFreeRegister++;
                int ptrReg = context.NextFreeRegister++;
                int iterReg = context.NextFreeRegister++;

                int counter = 0;
                foreach(var valExpr in initializer.Expressions)
                {
                    if (valExpr is ArrayCreationExpressionSyntax nestedArray)
                    {
                        var nestedTypeSymbol = context.SemanticModel.GetTypeInfo(nestedArray).Type;
                        var nestedSizeExpr = nestedArray.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault();
                        EmitArrayCreation(nestedArray.Initializer, nestedSizeExpr, nestedTypeSymbol, valReg, context, tempOffset);
                    }
                    else if (valExpr is ImplicitArrayCreationExpressionSyntax nestedImplArray)
                    {
                        var nestedTypeSymbol = context.SemanticModel.GetTypeInfo(nestedImplArray).Type;
                        EmitArrayCreation(nestedImplArray.Initializer, null, nestedTypeSymbol, valReg, context, tempOffset);
                    }
                    else
                    {
                        EmitExpression(valExpr, valReg, context, tempOffset);
                    }

                    EmitMovImmediate(idxReg, counter, context);
                    EmitMovImmediate(iterReg, elementSize, context);
                    EmitArithmeticOp(SyntaxKind.MultiplyExpression, idxReg, idxReg, iterReg, context);
                    EmitOpWithImmediate(SyntaxKind.AddExpression, idxReg, idxReg, headerSize, context);
                    EmitArithmeticOp(SyntaxKind.AddExpression, ptrReg, arrayAllocReg, idxReg, context);

                    if (elementSize == 1) context.Emit($"STRB r{valReg}, [r{ptrReg}, #0]");
                    else if (elementSize == 2) context.Emit($"STRH r{valReg}, [r{ptrReg}, #0]");
                    else context.Emit($"STR r{valReg}, [r{ptrReg}, #0]"); // Ignoring 8-byte structs for now 

                    counter++;
                }

                context.NextFreeRegister -= 4;
            }

            if (targetReg == 0) context.NextFreeRegister--; // arrayAllocReg
            context.NextFreeRegister -= 2; // sizeReg, lenReg
        }

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
            // Левая часть: используйте текущий свободный регистр (r0, r1...)
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
                if (context.Class.Global.Backend.TryGetAsConstant(context, id, out object constVal))
                {
                    EmitMovImmediate(rightTemp, (int)constVal, context); // Грузим константу в r(rightTemp)
                    EmitArithmeticOp(node.Kind(), targetReg, leftReg, rightTemp, context);
                }
                // 2. Если это переменная (a, b...)
                else if (context.RegisterMap.TryGetValue(id.Identifier.Text, out int rightReg))
                {
                    EmitArithmeticOp(node.Kind(), targetReg, leftReg, rightReg, context);
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

            if (context.Class.Global.Backend.TryGetAsConstant(context, expr, out object val))
            {
                EmitMovImmediate(tempOffset, (int)val, context);
                return tempOffset;
            }

            EmitExpression(expr, tempOffset, context, tempOffset + 1);
            return tempOffset;
        }

        public static void EmitOpWithImmediate(SyntaxKind op, int target, int left, int value, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitOpWithImmediate(context, op, target, left, value);
        }
        public static void EmitArithmeticOp(SyntaxKind op, int target, int left, int right, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitArithmeticOp(context, op, target, left, right);
        }

        public static void EmitMovRegister(int target, int source, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitMovRegister(context, target, source);
        }

        public static void EmitLdrImmediate(int target, int offset, MethodCompilationContext context)
        {
            // Placeholder: currently not strictly needed without baseReg. Actually, let's just make EmitMemoryAccess.
        }

        public static void EmitAddressOf(ExpressionSyntax expr, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            context.Class.Global.Backend.EmitAddressOf(context, expr, targetReg, tempOffset);
        }

        public static void EmitMemoryAccess(bool isLoad, int targetReg, int baseReg, int offset, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitMemoryAccess(context, isLoad, targetReg, baseReg, offset);
        }

        // Базовый MOV (Rd = Imm8)
        public static void EmitMovImmediate(int reg, int val, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitMovImmediate(context, reg, val);
        }
        public static void EmitDivide(int target, int left, int right, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitArithmeticOp(context, SyntaxKind.DivideExpression, target, left, right);
        }

        public static void EmitLogicalCondition(ExpressionSyntax condition, string trueLabel, string falseLabel, MethodCompilationContext context)
        {
            context.Class.Global.Backend.EmitLogicalCondition(context, condition, trueLabel, falseLabel);
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
                else if (context.StackMap.TryGetValue(id.Identifier.Text, out var sv))
                {
                    EmitMemoryAccess(true, targetReg, 13, sv.StackOffset, context);
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
            else if (expr is ConditionalExpressionSyntax ternary)
            {
                // Создаем уникальные метки для этого конкретного тернарника
                string falseLabel = context.NextLabel("TERN_FALSE");
                string endLabel = context.NextLabel("TERN_END");

                // 1. Вычисляем условие. Если оно ложно — прыгам на falseLabel
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
            else if (expr is ElementAccessExpressionSyntax elementAccess)
            {
                int addrReg = context.NextFreeRegister++;
                EmitAddressOf(elementAccess, addrReg, context, tempOffset);

                var arrayTypeSymbol = context.SemanticModel.GetTypeInfo(elementAccess.Expression).Type as IArrayTypeSymbol;
                var elType = arrayTypeSymbol?.ElementType;

                int elementSize = 4; // Assume 4 bytes for now
                if (elType != null)
                {
                    if (elType.SpecialType == SpecialType.System_Byte || elType.SpecialType == SpecialType.System_SByte || elType.SpecialType == SpecialType.System_Boolean) elementSize = 1;
                    else if (elType.SpecialType == SpecialType.System_Int16 || elType.SpecialType == SpecialType.System_UInt16 || elType.SpecialType == SpecialType.System_Char) elementSize = 2;
                    else if (elType.SpecialType == SpecialType.System_Int64 || elType.SpecialType == SpecialType.System_UInt64 || elType.SpecialType == SpecialType.System_Double) elementSize = 8;
                    else if (elType.TypeKind == TypeKind.Struct)
                        elementSize = Math.Max(1, elType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                if (elementSize == 1)
                {
                    context.Emit($"LDRB r{targetReg}, [r{addrReg}, #0]");
                    context.Write16((ushort)(0x7800 | ((addrReg & 0x7) << 3) | (targetReg & 0x7)));
                }
                else if (elementSize == 2)
                {
                    context.Emit($"LDRH r{targetReg}, [r{addrReg}, #0]");
                    context.Write16((ushort)(0x8800 | ((addrReg & 0x7) << 3) | (targetReg & 0x7)));
                }
                else
                {
                    context.Emit($"LDR r{targetReg}, [r{addrReg}, #0]");
                    context.Write16((ushort)(0x6800 | ((addrReg & 0x7) << 3) | (targetReg & 0x7)));
                }

                context.NextFreeRegister--;
            }
            else if (expr is ArrayCreationExpressionSyntax arrayCreation)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(arrayCreation).Type;
                var sizeExpr = arrayCreation.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault();
                EmitArrayCreation(arrayCreation.Initializer, sizeExpr, typeSymbol, targetReg, context, tempOffset);
            }
            else if (expr is ImplicitArrayCreationExpressionSyntax implicitArray)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(implicitArray).Type;
                EmitArrayCreation(implicitArray.Initializer, null, typeSymbol, targetReg, context, tempOffset);
            }
            else if (expr is ObjectCreationExpressionSyntax objectCreation)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(objectCreation).Type;
                
                if (typeSymbol != null && typeSymbol.TypeKind == TypeKind.Delegate)
                {
                    var ctorArg = objectCreation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                    var symInfo = ctorArg != null ? context.SemanticModel.GetSymbolInfo(ctorArg).Symbol : null;
                    var memGrp = ctorArg != null ? context.SemanticModel.GetMemberGroup(ctorArg).FirstOrDefault() : null;

                    var targetMethodCtor = symInfo as IMethodSymbol ?? memGrp as IMethodSymbol;
                    if (targetMethodCtor != null)
                    {
                        EmitDelegateCreation(targetMethodCtor, ctorArg, typeSymbol, targetReg, context, tempOffset);
                        return;
                    }
                }

                int size = 4;
                bool hasHeader = false;
                if (typeSymbol != null)
                {
                    hasHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true && typeSymbol.IsReferenceType;
                    if (hasHeader) size = 4; else size = 0;
                    int fieldsCount = typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count();
                    size += fieldsCount * 4;
                    if (size == 0) size = 4; 
                }

                EmitMovImmediate(0, size, context);
                EmitCall("NETMCU__Memory__Alloc", context, isStatic: true, isNative: true);

                if (hasHeader)
                {
                    string targetName = typeSymbol.ToDisplayString();
                    string symbolName = context.Class.Global.RegisterTypeLiteral(typeSymbol);
                    context.Class.Global.AddDataRelocation(context, symbolName, (int)context.Bin.Length);

                    int tmpReg = context.NextFreeRegister++;
                    context.Emit($"LDR r{tmpReg}, ={symbolName} ; (placeholder for MOVW/MOVT)");
                    context.Bin.Write(new byte[8], 0, 8);
                    context.Emit($"STR r{tmpReg}, [r0, #0]");
                    context.NextFreeRegister--;
                }

                if (targetReg != 0) EmitMovRegister(targetReg, 0, context);

                var ctorSymbol = context.SemanticModel.GetSymbolInfo(objectCreation).Symbol as IMethodSymbol;
                if (ctorSymbol != null && !ctorSymbol.IsImplicitlyDeclared && objectCreation.ArgumentList != null)
                {
                    var args = objectCreation.ArgumentList.Arguments;
                    List<int> argRegs = new();
                    for (int i = 0; i < args.Count; i++)
                    {
                        var argument = args[i];
                        int safeReg = context.NextFreeRegister++;
                        if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                            EmitAddressOf(argument.Expression, safeReg, context, tempOffset);
                        else
                            EmitExpression(argument.Expression, safeReg, context, tempOffset);
                        argRegs.Add(safeReg);
                    }

                    int stackArgsCount = 0;
                    for (int i = args.Count - 1; i >= 0; i--)
                    {
                        int targetArgReg = i + 1;
                        if (targetArgReg > 3)
                        {
                            context.Emit($"PUSH {{r{argRegs[i]}}}");
                            context.Write16((ushort)(0xB400 | (1 << argRegs[i])));
                            stackArgsCount++;
                        }
                    }

                    if (targetReg != 0) EmitMovRegister(0, targetReg, context);

                    for (int i = 0; i < args.Count; i++)
                    {
                        int targetArgReg = i + 1;
                        if (targetArgReg <= 3)
                            if (argRegs[i] != targetArgReg) EmitMovRegister(targetArgReg, argRegs[i], context);
                    }

                    EmitCall(ctorSymbol.ToDisplayString(), context, isStatic: false, isNative: false);

                    context.NextFreeRegister -= args.Count;

                    if (stackArgsCount > 0)
                    {
                        context.Emit($"ADD SP, SP, #{stackArgsCount * 4}");
                        uint imm12 = (uint)(stackArgsCount * 4);
                        uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                        context.Write32(op);
                    }

                    if (targetReg != 0) EmitMovRegister(targetReg, 0, context); // restoring allocated ptr
                }
            }
            else if (expr is InvocationExpressionSyntax invocation)
            {
                var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol 
                                ?? context.SemanticModel.GetMemberGroup(invocation).FirstOrDefault() as IMethodSymbol;

                var delegateType = context.SemanticModel.GetTypeInfo(invocation.Expression).Type;
                bool isDelegateInvoke = delegateType != null && delegateType.TypeKind == TypeKind.Delegate;
                if (!isDelegateInvoke && methodSymbol != null && methodSymbol.ContainingType?.TypeKind == TypeKind.Delegate)
                {
                    isDelegateInvoke = true; // Handle case where methodSymbol is Delegate.Invoke
                }

                if (methodSymbol == null || isDelegateInvoke)
                {
                    if (isDelegateInvoke)
                    {
                        int delegateReg = context.NextFreeRegister++;
                        var delegateExpression = invocation.Expression;
                        if (delegateExpression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Invoke")
                        {
                            delegateExpression = ma.Expression;
                        }

                        EmitExpressionInternal(delegateExpression, delegateReg, context, tempOffset);

                        int targetOffset = context.Class.Global.BuildingContext.Options?.TypeHeader == true ? 4 : 0;
                        int ptrOffset = targetOffset + 4;
                        if (context.Class.Global.Childs.TryGetValue("System.Delegate", out var delTypeCtx) && delTypeCtx is TypeCompilationContext delTcc)
                        {
                            delTcc.FieldOffsets.TryGetValue("Target", out targetOffset);
                            delTcc.FieldOffsets.TryGetValue("MethodPtr", out ptrOffset);
                        }

                        var delArgs = invocation.ArgumentList.Arguments;
                        List<int> delArgRegs = new();
                        for (int i = 0; i < delArgs.Count; i++)
                        {
                            var argument = delArgs[i];
                            int safeReg = context.NextFreeRegister++;
                            if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                                EmitAddressOf(argument.Expression, safeReg, context, tempOffset);
                            else
                                EmitExpression(argument.Expression, safeReg, context, tempOffset);
                            delArgRegs.Add(safeReg);
                        }

                        int delStackArgsCount = 0;
                        for (int i = delArgs.Count - 1; i >= 0; i--)
                        {
                            int targetArgReg = i + 1;
                            if (targetArgReg > 3)
                            {
                                context.Emit($"PUSH {{r{delArgRegs[i]}}}");
                                context.Write16((ushort)(0xB400 | (1 << delArgRegs[i])));
                                delStackArgsCount++;
                            }
                        }

                        for (int i = 0; i < delArgs.Count; i++)
                        {
                            int targetArgReg = i + 1;
                            if (targetArgReg <= 3)
                                if (delArgRegs[i] != targetArgReg) EmitMovRegister(targetArgReg, delArgRegs[i], context);
                        }

                        context.Emit($"LDR r0, [r{delegateReg}, #{targetOffset}] @ Load Target");
                        int methodPtrReg = context.NextFreeRegister++;
                        context.Emit($"LDR r{methodPtrReg}, [r{delegateReg}, #{ptrOffset}] @ Load MethodPtr");

                        context.Emit($"BLX r{methodPtrReg} @ Invoke Delegate");
                        context.Write16((ushort)(0x4780 | (methodPtrReg << 3)));

                        context.NextFreeRegister--; // interfaceFuncPtrReg
                        context.NextFreeRegister -= delArgs.Count;

                        if (delStackArgsCount > 0)
                        {
                            context.Emit($"ADD SP, SP, #{delStackArgsCount * 4}");
                            uint imm12 = (uint)(delStackArgsCount * 4);
                            uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                            context.Write32(op);
                        }

                        if (targetReg != 0) EmitMovRegister(targetReg, 0, context);
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

                var args = invocation.ArgumentList.Arguments;
                List<int> argRegs = new();
                for (int i = 0; i < args.Count; i++)
                {
                    var argument = args[i];
                    int safeReg = context.NextFreeRegister++;

                    if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                        EmitAddressOf(argument.Expression, safeReg, context, tempOffset);
                    else
                        EmitExpression(argument.Expression, safeReg, context, tempOffset);
                    argRegs.Add(safeReg);
                }

                int stackArgsCount = 0;
                for (int i = args.Count - 1; i >= 0; i--)
                {
                    int targetArgReg = i + regOffset; 
                    if (targetArgReg > 3)
                    {
                        context.Emit($"PUSH {{r{argRegs[i]}}}");
                        context.Write16((ushort)(0xB400 | (1 << argRegs[i])));
                        stackArgsCount++;
                    }
                }

                if (!methodSymbol.IsStatic)
                    EmitMovRegister(0, instanceReg, context); // set 'this'

                for (int i = 0; i < args.Count; i++)
                {
                    int targetArgReg = i + regOffset;
                    if (targetArgReg <= 3)
                        if (argRegs[i] != targetArgReg) EmitMovRegister(targetArgReg, argRegs[i], context);
                }

                if (isInterfaceCall)
                {
                    context.Emit($"BLX r{interfaceFuncPtrReg}");
                    context.Write16((ushort)(0x4780 | (interfaceFuncPtrReg << 3)));
                    context.NextFreeRegister--; // interfaceFuncPtrReg
                }
                else
                {
                    string nativeFunctionName = methodSymbol.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                        .ConstructorArguments.FirstOrDefault().Value?.ToString();

                    string callTarget = nativeFunctionName ?? methodSymbol.ToDisplayString();
                    EmitCall(callTarget, context, methodSymbol.IsStatic, nativeFunctionName != null);
                }

                context.NextFreeRegister -= args.Count;
                if (!methodSymbol.IsStatic) context.NextFreeRegister--; // instanceReg

                if (stackArgsCount > 0)
                {
                    context.Emit($"ADD SP, SP, #{stackArgsCount * 4}");
                    uint imm12 = (uint)(stackArgsCount * 4);
                    uint op = 0xF10D0D00 | (((imm12 >> 11) & 1) << 26) | (((imm12 >> 8) & 7) << 12) | (imm12 & 0xFF);
                    context.Write32(op);
                }

                if (targetReg != 0) EmitMovRegister(targetReg, 0, context);
            }
        }

        private static void EmitCompare(int left, int right, MethodCompilationContext context) => context.Class.Global.Backend.EmitCompare(context, left, right);
        private static void EmitCompareImmediate(int reg, int imm, MethodCompilationContext context) => context.Class.Global.Backend.EmitCompareImmediate(context, reg, imm);
        private static void EmitBranch(string label, string condition, MethodCompilationContext context) => context.Class.Global.Backend.EmitBranch(context, label, condition);
        private static void EmitJump(string label, MethodCompilationContext context) => context.Class.Global.Backend.EmitJump(context, label);
        private static void EmitCall(string name, MethodCompilationContext context, bool isStatic, bool isNative = false) => context.Class.Global.Backend.EmitCall(context, name, isStatic, isNative);
        private static int ParseLiteral(LiteralExpressionSyntax literal, MethodCompilationContext context) => context.Class.Global.Backend.ParseLiteral(context.Class.Global, literal);
    }
}
