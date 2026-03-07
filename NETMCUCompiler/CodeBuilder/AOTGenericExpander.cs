using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    public class AOTGenericExpander
    {
        public static Compilation Expand(Compilation compilation)
        {
            // First pass: Find all constructed generic types
            var genericTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var syntaxTreesList = compilation.SyntaxTrees.ToList();

            foreach (var tree in syntaxTreesList)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                var typeInfos = root.DescendantNodes()
                    .Select(n => model.GetTypeInfo(n).Type)
                    .Concat(root.DescendantNodes().Select(n => model.GetSymbolInfo(n).Symbol as ITypeSymbol));

                foreach (var type in typeInfos.OfType<INamedTypeSymbol>())
                {
                    if (type.IsGenericType && !type.IsDefinition)
                    {
                        genericTypes.Add(type);
                    }
                }
            }

            if (!genericTypes.Any())
                return compilation;

            // Group by original definition
            var byDefinition = genericTypes.GroupBy(g => g.OriginalDefinition, SymbolEqualityComparer.Default).ToList();

            List<SyntaxTree> newTrees = new List<SyntaxTree>();

            // For each original syntax tree, we will generate a modified one
            foreach (var tree in syntaxTreesList)
            {
                var model = compilation.GetSemanticModel(tree);
                var rewriter = new GenericUsageRewriter(model, genericTypes);
                var newRoot = rewriter.Visit(tree.GetRoot());
                
                if (tree.FilePath.Contains("Program.cs"))
                {
                    Console.WriteLine("=== REWRITTEN Program.cs ===");
                    Console.WriteLine(newRoot.ToFullString());
                    Console.WriteLine("============================");
                }

                newTrees.Add(tree.WithRootAndOptions(newRoot, tree.Options));
            }

            // Generate definitions for all used generic types
            var generatedMembers = new List<MemberDeclarationSyntax>();
            var generatedNames = new HashSet<string>();

            foreach (var group in byDefinition)
            {
                var originalDef = group.Key as INamedTypeSymbol;
                if (originalDef == null) continue;

                var defRefs = originalDef.DeclaringSyntaxReferences;
                if (defRefs.Length == 0) continue;

                var declSyntax = defRefs[0].GetSyntax() as TypeDeclarationSyntax;
                if (declSyntax == null) continue;

                var uniqueInstances = group.DistinctBy(i => GetCleanName(i)).ToList();
                foreach (var instance in uniqueInstances)
                {
                    string cleanName = GetCleanName(instance);
                    if (!generatedNames.Add(cleanName))
                        continue;

                    // Map type parameters to arguments
                    var typeMap = new Dictionary<string, ITypeSymbol>();
                    for (int i = 0; i < originalDef.TypeParameters.Length; i++)
                    {
                        typeMap[originalDef.TypeParameters[i].Name] = instance.TypeArguments[i];
                    }

                    // Rewrite the class declaration
                    var instanceRewriter = new GenericDefinitionRewriter(typeMap, GetCleanName(instance));
                    var newDecl = (TypeDeclarationSyntax)instanceRewriter.Visit(declSyntax);
                    
                    // Remove generic parameters
                    newDecl = newDecl.WithTypeParameterList(null);

                    // Add to our generated members
                    if (newDecl != null)
                    {
                        generatedMembers.Add(newDecl);
                    }
                }
            }

            if (generatedMembers.Any())
            {
                var generatedNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName("NETMCU_Generated_Generics"))
                    .WithMembers(SyntaxFactory.List(generatedMembers));
                var generatedRoot = SyntaxFactory.CompilationUnit().WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(generatedNamespace)).NormalizeWhitespace();

                Console.WriteLine("=== GENERATED GENERICS ===");
                Console.WriteLine(generatedRoot.ToFullString());
                Console.WriteLine("==========================");

                var generatedTree = CSharpSyntaxTree.Create(generatedRoot, syntaxTreesList.FirstOrDefault()?.Options as CSharpParseOptions);
                newTrees.Add(generatedTree);
            }

            // Create new compilation
            var newCompilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTrees);

            var diagnostics = newCompilation.GetDiagnostics();
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                Console.WriteLine("=== AOT GENERICS COMPILATION ERRORS ===");
                foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    Console.WriteLine(d.ToString());
                }
                Console.WriteLine("=======================================");
            }

            // Removing generic classes from new compilation is tricky (they might be referenced by other non-expanded files),
            // We just let them be, but we've rewritten usages to use our generated non-generic classes.
            return newCompilation;
        }

        public static string GetCleanName(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var args = string.Join("_", named.TypeArguments.Select(t => GetCleanName(t)));
                return $"{named.Name}_{args}";
            }
            return type.Name;
        }
    }

    class GenericUsageRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly HashSet<INamedTypeSymbol> _knownGenerics;

        public GenericUsageRewriter(SemanticModel model, HashSet<INamedTypeSymbol> knownGenerics)
        {
            _model = model;
            _knownGenerics = knownGenerics;
        }

        public override SyntaxNode VisitGenericName(GenericNameSyntax node)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol as INamedTypeSymbol;
            if (symbol != null && symbol.IsGenericType && !symbol.IsDefinition)
            {
                var cleanName = AOTGenericExpander.GetCleanName(symbol);
                return SyntaxFactory.ParseTypeName($"global::NETMCU_Generated_Generics.{cleanName}").WithTriviaFrom(node);
            }
            return base.VisitGenericName(node);
        }
    }

    class GenericDefinitionRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, ITypeSymbol> _typeMap;
        private readonly string _newName;

        public GenericDefinitionRewriter(Dictionary<string, ITypeSymbol> typeMap, string newName)
        {
            _typeMap = typeMap;
            _newName = newName;
        }

        public override SyntaxNode VisitTypeParameterConstraintClause(TypeParameterConstraintClauseSyntax node)
        {
            // Remove constraints for the replaced generic type parameters.
            // If the constraint is for a method-level generic parameter, this might incorrectly remove it,
            // but for simplicity we remove all constraint clauses in the expanded generic type.
            return null;
        }

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);
            if (visited.TypeParameterList != null) // It's the main class
            {
                return visited.WithIdentifier(SyntaxFactory.Identifier(_newName))
                              .WithTypeParameterList(null)
                              .WithConstraintClauses(SyntaxFactory.List<TypeParameterConstraintClauseSyntax>());
            }
            return visited;
        }

        public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
        {
            var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node);
            if (visited.TypeParameterList != null) // It's the main struct
            {
                return visited.WithIdentifier(SyntaxFactory.Identifier(_newName))
                              .WithTypeParameterList(null)
                              .WithConstraintClauses(SyntaxFactory.List<TypeParameterConstraintClauseSyntax>());
            }
            return visited;
        }

        public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node);
            return visited.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }

        public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (_typeMap.TryGetValue(node.Identifier.Text, out var typeArg))
            {
                return SyntaxFactory.ParseTypeName(typeArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }
    }
}
