using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace NETMCUCompiler.CodeBuilder
{
    public record ArrayCreationInfo(
        ITypeSymbol TypeSymbol,
        ExpressionSyntax SizeExpression,
        InitializerExpressionSyntax Initializer,
        int ElementSize,
        bool HasTypeHeader,
        int HeaderSize,
        string SymbolName,
        int TargetReg
    );

    public record DelegateCreationInfo(
        ITypeSymbol TypeSymbol,
        IMethodSymbol TargetMethod,
        ExpressionSyntax NodeExpression,
        bool HasTypeHeader,
        string SymbolName,
        int TargetOffset,
        int PtrOffset,
        int Size,
        int TargetReg
    );

    public record ObjectCreationInfo(
        ITypeSymbol TypeSymbol,
        IMethodSymbol CtorSymbol,
        IReadOnlyList<ArgumentSyntax> Arguments,
        bool HasTypeHeader,
        int Size,
        string SymbolName,
        int TargetReg
    );

    public record InvocationInfo(
        IMethodSymbol MethodSymbol,
        ExpressionSyntax InstanceExpression,
        IReadOnlyList<ArgumentSyntax> Arguments,
        bool IsDelegateInvoke,
        bool IsInterfaceCall,
        bool IsVirtualCall,
        bool IsBaseCall,
        string NativeFunctionName,
        int TargetReg
    );
}