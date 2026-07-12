using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// The Testing harness drives the real filter classes (internal) directly.
[assembly: InternalsVisibleTo("vebege_testing")]
[assembly: InternalsVisibleTo("vebege_live")]

[assembly: AssemblyTitle("VeBeGe")]
[assembly: AssemblyDescription("VeBeGe, virtual background virtual cameras that just work")]
[assembly: AssemblyProduct("VeBeGe")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: ComVisible(false)]
[assembly: Guid("c3d40001-0001-4b01-8e01-000000000011")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
