using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: AndroidAssemblyPatcher <Assembly-CSharp.dll>");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath)!);
var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
{
    ReadWrite = false,
    AssemblyResolver = resolver,
});
var candidates = AllTypes(assembly.MainModule.Types)
    .Where(type => type.Name.StartsWith("<Start>", StringComparison.Ordinal)
        && type.Methods.Any(method => method.Name == "MoveNext")
        && type.DeclaringType?.Name == "GlobalSceneLoader")
    .ToList();

if (candidates.Count != 1)
{
    Console.Error.WriteLine($"Expected one GlobalSceneLoader.Start iterator, found {candidates.Count}; left assembly unchanged.");
    return 3;
}

var moveNext = candidates[0].Methods.Single(method => method.Name == "MoveNext");
var instructions = moveNext.Body.Instructions;
var gate = instructions.FirstOrDefault(instruction =>
    (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
    && instruction.Operand is MethodReference method
    && method.Name == "Contains"
    && method.DeclaringType.FullName == "System.String"
    && instruction.Previous?.OpCode == OpCodes.Ldstr
    && string.Equals(instruction.Previous.Operand as string, "nvidia", StringComparison.OrdinalIgnoreCase));

if (gate?.Next is null || gate.Next.Operand is not Instruction normalStartup ||
    (gate.Next.OpCode != OpCodes.Brtrue && gate.Next.OpCode != OpCodes.Brtrue_S))
{
    Console.Error.WriteLine("Could not identify the NVIDIA-only startup guard; left assembly unchanged.");
    return 4;
}

// The original APK exits on every GPU vendor except NVIDIA Tegra. Branch straight to
// normal startup so the game can be tried on other Android GPUs; rendering compatibility
// still depends on the device's support for this 2012 Unity build and its shaders.
gate.Next.OpCode = OpCodes.Br;

var temporaryPath = assemblyPath + ".nekonexus.tmp";
assembly.Write(temporaryPath);
assembly.Dispose();
File.Move(temporaryPath, assemblyPath, overwrite: true);
Console.WriteLine("Bypassed the Tegra-only startup check in the working APK copy.");
return 0;

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes))
            yield return nested;
    }
}
