using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ObjectAnalyzer.TypeSystem;

public class DynamicAssemblyContext() : AssemblyLoadContext(name: $"DynamicType_{Guid.NewGuid():N}", isCollectible: true), IDisposable
{
    public void Dispose()
    {
        Unload();
        GC.SuppressFinalize(this);
    }
}
