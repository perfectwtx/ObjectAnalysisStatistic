using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ObjectAnalyzer.TypeSystem;

public class DynamicAssemblyContext : AssemblyLoadContext, IDisposable
{
    public DynamicAssemblyContext() : base(name: $"DynamicType_{Guid.NewGuid():N}", isCollectible: true) { }

    public void Dispose()
    {
        Unload();
        GC.SuppressFinalize(this);
    }
}
