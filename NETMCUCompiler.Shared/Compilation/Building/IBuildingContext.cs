using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NETMCUCompiler.Shared.Compilation.Building
{
    public interface IBuildingContext
    {
        string BinPath { get; }
        Microsoft.CodeAnalysis.Compilation Compilation { get; }
        Dictionary<string, long> CoreSymbols { get; }
        string ObjPath { get; }
        BuildingOptions? Options { get; }
        string Path { get; }
        IMethodSymbol? ProgramMainMethod { get; }
        MethodDeclarationSyntax? ProgramMainNode { get; }
        INamedTypeSymbol? ProgramMainType { get; }
        ClassDeclarationSyntax? ProgramMainTypeNode { get; }
        SemanticModel? ProgramSemanticModel { get; }
        string RootPath { get; }

        Task<bool> BuildCore();
        Task<bool> Compile();
        Task LoadAsync();
    }
}