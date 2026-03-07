using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    public static class VTableBuilder
    {
        public static List<IMethodSymbol> GetVTable(ITypeSymbol typeSymbol)
        {
            var vtableInfo = new List<IMethodSymbol>();
            var hierarchy = new List<ITypeSymbol>();
            
            var current = typeSymbol;
            while (current != null)
            {
                hierarchy.Insert(0, current);
                current = current.BaseType;
            }

            foreach (var t in hierarchy)
            {
                foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
                {
                    if (m.IsVirtual || m.IsAbstract || m.IsOverride)
                    {
                        if (m.IsOverride && m.OverriddenMethod != null)
                        {
                            var overridden = m.OverriddenMethod.OriginalDefinition;
                            int index = vtableInfo.FindIndex(x => SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, overridden));
                            
                            if (index >= 0)
                            {
                                vtableInfo[index] = m;
                            }
                            else
                            {
                                // If for some reason we cannot find the exact base method, we might need to search deeper, 
                                // but OriginalDefinition usually works. We fall back to adding it.
                                vtableInfo.Add(m);
                            }
                        }
                        else if (m.IsVirtual || m.IsAbstract)
                        {
                            vtableInfo.Add(m);
                        }
                    }
                }
            }
            
            return vtableInfo;
        }

        public static int GetVTableIndex(IMethodSymbol methodSymbol)
        {
            var type = methodSymbol.ContainingType;
            if (type == null) return -1;
            
            var vtable = GetVTable(type);
            var original = methodSymbol.OriginalDefinition;
            
            // First try exact match
            int index = vtable.FindIndex(m => SymbolEqualityComparer.Default.Equals(m, methodSymbol));
            if (index >= 0) return index;

            // Then try original definition
            index = vtable.FindIndex(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, original));
            return index;
        }
    }
}
