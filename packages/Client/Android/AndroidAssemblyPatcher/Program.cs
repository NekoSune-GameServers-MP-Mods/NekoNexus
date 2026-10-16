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

if (gate?.Next is null)
{
    Console.Error.WriteLine("Could not identify the NVIDIA-only startup guard; left assembly unchanged.");
    return 4;
}

// Some 4.3.10 APK revisions use Contains("nvidia") as a conditional Tegra-only startup
// guard; others leave the old check as a discarded expression. Force only the guarded
// version true by searching for the empty string, preserving the boolean stack behavior.
if ((gate.Next.OpCode == OpCodes.Brtrue || gate.Next.OpCode == OpCodes.Brtrue_S || gate.Next.OpCode == OpCodes.Br)
    && gate.Next.Operand is Instruction)
{
    gate.Previous.Operand = string.Empty;
    // Repair APKs produced by the earlier patcher, which changed brtrue to br and left
    // the Contains result on the evaluation stack.
    if (gate.Next.OpCode == OpCodes.Br)
        gate.Next.OpCode = OpCodes.Brtrue;
}
else if (gate.Next.OpCode != OpCodes.Pop)
{
    Console.Error.WriteLine("NVIDIA startup check has an unexpected IL shape; left assembly unchanged.");
    return 4;
}

// The Android 4.3.10 build has a 334x200 IMGUI login dialog, which becomes tiny on
// current high-density phone screens. Scale the login and registration dialogs around
// the screen center, preserving touch hit-testing because Unity applies GUI.matrix to
// both drawing and input coordinates.
var unityEngineReference = assembly.MainModule.AssemblyReferences
    .FirstOrDefault(reference => reference.Name == "UnityEngine");
if (unityEngineReference is null)
{
    Console.Error.WriteLine("Could not find UnityEngine reference; left panel layout unchanged.");
    return 5;
}

var unityEngine = resolver.Resolve(unityEngineReference);
var uiScale = CreateAndroidLoginScale(assembly.MainModule, unityEngine.MainModule);
var panels = new[] { "LoginPanelGUI", "SignupPanelGUI", "CompleteAccountPanelGUI" };
foreach (var panelName in panels)
{
    var panel = AllTypes(assembly.MainModule.Types).SingleOrDefault(type => type.Name == panelName);
    var onGui = panel?.Methods.SingleOrDefault(method => method.Name == "OnGUI" && method.HasBody);
    if (onGui is null)
    {
        Console.Error.WriteLine($"Could not find {panelName}.OnGUI; left assembly unchanged.");
        return 6;
    }

    InjectScaleCalls(onGui, uiScale.Begin, uiScale.End);
}

var completeAccountPanel = AllTypes(assembly.MainModule.Types).Single(type => type.Name == "CompleteAccountPanelGUI");
var completeAccountOnGui = completeAccountPanel.Methods.Single(method => method.Name == "OnGUI" && method.HasBody);
ReplaceNameTextField(completeAccountPanel, completeAccountOnGui, uiScale.EditName);
var loginPanel = AllTypes(assembly.MainModule.Types).Single(type => type.Name == "LoginPanelGUI");
var login = loginPanel.Methods.Single(method => method.Name == "Login" && method.HasBody && method.Parameters.Count == 2);
PersistRememberedLogin(assembly.MainModule, unityEngine.MainModule, login);

var temporaryPath = assemblyPath + ".nekonexus.tmp";
assembly.Write(temporaryPath);
assembly.Dispose();
File.Move(temporaryPath, assemblyPath, overwrite: true);
Console.WriteLine("Bypassed the Tegra-only startup check and enlarged Android login, signup, and name panels.");
Console.WriteLine("Persisted Remember Me credentials immediately after writing Android login preferences.");
return 0;

static (MethodDefinition Begin, MethodDefinition End, MethodDefinition EditName) CreateAndroidLoginScale(ModuleDefinition module, ModuleDefinition unity)
{
    TypeDefinition FindType(string fullName) => unity.GetType(fullName)
        ?? throw new InvalidOperationException($"UnityEngine type {fullName} was not found.");

    MethodReference FindMethod(string typeName, string methodName, int parameterCount, string? returnType = null) =>
        module.ImportReference(FindType(typeName).Methods.Single(method => method.Name == methodName
            && method.Parameters.Count == parameterCount
            && (returnType is null || method.ReturnType.FullName == returnType)));

    var matrixType = module.ImportReference(FindType("UnityEngine.Matrix4x4"));
    var vectorType = module.ImportReference(FindType("UnityEngine.Vector3"));
    var helper = new TypeDefinition(
        "NekoNexus.Android",
        "LoginUiScale",
        TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        module.TypeSystem.Object);
    var previousMatrix = new FieldDefinition("PreviousMatrix", FieldAttributes.Private | FieldAttributes.Static, matrixType);
    helper.Fields.Add(previousMatrix);
    var keyboardType = module.ImportReference(FindType("UnityEngine.TouchScreenKeyboard"));
    var nameKeyboard = new FieldDefinition("NameKeyboard", FieldAttributes.Private | FieldAttributes.Static, keyboardType);
    helper.Fields.Add(nameKeyboard);
    module.Types.Add(helper);

    var guiGetMatrix = FindMethod("UnityEngine.GUI", "get_matrix", 0);
    var guiSetMatrix = FindMethod("UnityEngine.GUI", "set_matrix", 1);
    var screenWidth = FindMethod("UnityEngine.Screen", "get_width", 0);
    var screenHeight = FindMethod("UnityEngine.Screen", "get_height", 0);
    var vectorConstructor = module.ImportReference(FindType("UnityEngine.Vector3").Methods.Single(method => method.IsConstructor && method.Parameters.Count == 3));
    var quaternionIdentity = FindMethod("UnityEngine.Quaternion", "get_identity", 0);
    var vectorOne = FindMethod("UnityEngine.Vector3", "get_one", 0);
    var matrixTrs = FindMethod("UnityEngine.Matrix4x4", "TRS", 3);
    var matrixMultiply = FindMethod("UnityEngine.Matrix4x4", "op_Multiply", 2, "UnityEngine.Matrix4x4");
    var min = FindMethod("UnityEngine.Mathf", "Min", 2, "System.Single");
    var clamp = FindMethod("UnityEngine.Mathf", "Clamp", 3, "System.Single");

    var begin = new MethodDefinition("Begin", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
    helper.Methods.Add(begin);
    var il = begin.Body.GetILProcessor();
    var centerX = new VariableDefinition(module.TypeSystem.Single);
    var centerY = new VariableDefinition(module.TypeSystem.Single);
    var scale = new VariableDefinition(module.TypeSystem.Single);
    begin.Body.Variables.Add(centerX);
    begin.Body.Variables.Add(centerY);
    begin.Body.Variables.Add(scale);
    begin.Body.InitLocals = true;

    il.Append(il.Create(OpCodes.Call, guiGetMatrix));
    il.Append(il.Create(OpCodes.Stsfld, previousMatrix));
    il.Append(il.Create(OpCodes.Call, screenWidth));
    il.Append(il.Create(OpCodes.Conv_R4));
    il.Append(il.Create(OpCodes.Ldc_R4, 0.5f));
    il.Append(il.Create(OpCodes.Mul));
    il.Append(il.Create(OpCodes.Stloc, centerX));
    il.Append(il.Create(OpCodes.Call, screenHeight));
    il.Append(il.Create(OpCodes.Conv_R4));
    il.Append(il.Create(OpCodes.Ldc_R4, 0.5f));
    il.Append(il.Create(OpCodes.Mul));
    il.Append(il.Create(OpCodes.Stloc, centerY));

    il.Append(il.Create(OpCodes.Call, screenWidth));
    il.Append(il.Create(OpCodes.Conv_R4));
    il.Append(il.Create(OpCodes.Ldc_R4, 900f));
    il.Append(il.Create(OpCodes.Div));
    il.Append(il.Create(OpCodes.Call, screenHeight));
    il.Append(il.Create(OpCodes.Conv_R4));
    il.Append(il.Create(OpCodes.Ldc_R4, 480f));
    il.Append(il.Create(OpCodes.Div));
    il.Append(il.Create(OpCodes.Call, min));
    il.Append(il.Create(OpCodes.Ldc_R4, 1.4f));
    il.Append(il.Create(OpCodes.Ldc_R4, 2.2f));
    il.Append(il.Create(OpCodes.Call, clamp));
    il.Append(il.Create(OpCodes.Stloc, scale));

    // T(center) * S(scale) * T(-center) * previousMatrix
    il.Append(il.Create(OpCodes.Ldloc, centerX));
    il.Append(il.Create(OpCodes.Ldloc, centerY));
    il.Append(il.Create(OpCodes.Ldc_R4, 0f));
    il.Append(il.Create(OpCodes.Newobj, vectorConstructor));
    il.Append(il.Create(OpCodes.Call, quaternionIdentity));
    il.Append(il.Create(OpCodes.Ldloc, scale));
    il.Append(il.Create(OpCodes.Ldloc, scale));
    il.Append(il.Create(OpCodes.Ldc_R4, 1f));
    il.Append(il.Create(OpCodes.Newobj, vectorConstructor));
    il.Append(il.Create(OpCodes.Call, matrixTrs));

    il.Append(il.Create(OpCodes.Ldloc, centerX));
    il.Append(il.Create(OpCodes.Neg));
    il.Append(il.Create(OpCodes.Ldloc, centerY));
    il.Append(il.Create(OpCodes.Neg));
    il.Append(il.Create(OpCodes.Ldc_R4, 0f));
    il.Append(il.Create(OpCodes.Newobj, vectorConstructor));
    il.Append(il.Create(OpCodes.Call, quaternionIdentity));
    il.Append(il.Create(OpCodes.Call, vectorOne));
    il.Append(il.Create(OpCodes.Call, matrixTrs));
    il.Append(il.Create(OpCodes.Call, matrixMultiply));
    il.Append(il.Create(OpCodes.Ldsfld, previousMatrix));
    il.Append(il.Create(OpCodes.Call, matrixMultiply));
    il.Append(il.Create(OpCodes.Call, guiSetMatrix));
    il.Append(il.Create(OpCodes.Ret));

    var end = new MethodDefinition("End", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
    helper.Methods.Add(end);
    end.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, previousMatrix));
    end.Body.Instructions.Add(Instruction.Create(OpCodes.Call, guiSetMatrix));
    end.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

    var keyboardOpen = module.ImportReference(FindType("UnityEngine.TouchScreenKeyboard").Methods.Single(method => method.Name == "Open"
        && method.IsStatic && method.Parameters.Count == 6 && method.Parameters[0].ParameterType.FullName == "System.String"));
    MethodReference KeyboardGetter(string name) => module.ImportReference(FindType("UnityEngine.TouchScreenKeyboard").Methods.Single(method => method.Name == "get_" + name && method.Parameters.Count == 0));
    var keyboardText = KeyboardGetter("text");
    var keyboardActive = KeyboardGetter("active");
    var keyboardDone = KeyboardGetter("done");
    var keyboardCanceled = KeyboardGetter("wasCanceled");
    var hideKeyboardInput = module.ImportReference(FindType("UnityEngine.TouchScreenKeyboard").Methods.Single(method => method.Name == "set_hideInput"
        && method.IsStatic && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.Boolean"));
    var nameFieldButton = module.ImportReference(FindType("UnityEngine.GUI").Methods.Single(method => method.Name == "Button"
        && method.ReturnType.FullName == "System.Boolean" && method.Parameters.Count == 3
        && method.Parameters[0].ParameterType.FullName == "UnityEngine.Rect"
        && method.Parameters[1].ParameterType.FullName == "System.String"
        && method.Parameters[2].ParameterType.FullName == "UnityEngine.GUIStyle"));
    var editName = new MethodDefinition("EditName", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
    editName.Parameters.Add(new ParameterDefinition("position", ParameterAttributes.None, module.ImportReference(FindType("UnityEngine.Rect"))));
    editName.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.String));
    editName.Parameters.Add(new ParameterDefinition("maxLength", ParameterAttributes.None, module.TypeSystem.Int32));
    editName.Parameters.Add(new ParameterDefinition("style", ParameterAttributes.None, module.ImportReference(FindType("UnityEngine.GUIStyle"))));
    helper.Methods.Add(editName);
    var nameIl = editName.Body.GetILProcessor();
    var result = new VariableDefinition(module.TypeSystem.String);
    editName.Body.Variables.Add(result);
    editName.Body.InitLocals = true;
    var noKeyboard = Instruction.Create(OpCodes.Ldsfld, nameKeyboard);
    var keyboardActiveLabel = Instruction.Create(OpCodes.Ldsfld, nameKeyboard);
    var clearKeyboard = Instruction.Create(OpCodes.Ldnull);
    var limitLength = Instruction.Create(OpCodes.Ldloc, result);
    var returnValue = Instruction.Create(OpCodes.Ldloc, result);

    nameIl.Append(nameIl.Create(OpCodes.Ldarg_1));
    nameIl.Append(nameIl.Create(OpCodes.Stloc, result));
    nameIl.Append(nameIl.Create(OpCodes.Ldarg_0));
    nameIl.Append(nameIl.Create(OpCodes.Ldarg_1));
    nameIl.Append(nameIl.Create(OpCodes.Ldarg_3));
    nameIl.Append(nameIl.Create(OpCodes.Call, nameFieldButton));
    nameIl.Append(nameIl.Create(OpCodes.Brfalse, noKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0));
    nameIl.Append(nameIl.Create(OpCodes.Call, hideKeyboardInput));
    nameIl.Append(nameIl.Create(OpCodes.Ldarg_1));
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0)); // TouchScreenKeyboardType.Default
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0)); // autocorrection
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0)); // multiline
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0)); // secure
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0)); // alert
    nameIl.Append(nameIl.Create(OpCodes.Call, keyboardOpen));
    nameIl.Append(nameIl.Create(OpCodes.Stsfld, nameKeyboard));

    nameIl.Append(noKeyboard);
    nameIl.Append(nameIl.Create(OpCodes.Brfalse, limitLength));
    nameIl.Append(nameIl.Create(OpCodes.Ldsfld, nameKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, keyboardDone));
    nameIl.Append(nameIl.Create(OpCodes.Brfalse, keyboardActiveLabel));
    nameIl.Append(nameIl.Create(OpCodes.Ldsfld, nameKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, keyboardCanceled));
    nameIl.Append(nameIl.Create(OpCodes.Brtrue, clearKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Ldsfld, nameKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, keyboardText));
    nameIl.Append(nameIl.Create(OpCodes.Stloc, result));
    nameIl.Append(nameIl.Create(OpCodes.Br, clearKeyboard));

    nameIl.Append(keyboardActiveLabel);
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, keyboardActive));
    nameIl.Append(nameIl.Create(OpCodes.Brfalse, clearKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Ldsfld, nameKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, keyboardText));
    nameIl.Append(nameIl.Create(OpCodes.Stloc, result));
    nameIl.Append(nameIl.Create(OpCodes.Br, limitLength));

    nameIl.Append(clearKeyboard);
    nameIl.Append(nameIl.Create(OpCodes.Stsfld, nameKeyboard));
    nameIl.Append(nameIl.Create(OpCodes.Br, limitLength));

    var stringLength = module.ImportReference(module.TypeSystem.String.Resolve().Methods.Single(method => method.Name == "get_Length" && method.Parameters.Count == 0));
    var substring = module.ImportReference(module.TypeSystem.String.Resolve().Methods.Single(method => method.Name == "Substring" && method.Parameters.Count == 2
        && method.Parameters.All(parameter => parameter.ParameterType.FullName == "System.Int32")));
    nameIl.Append(limitLength);
    nameIl.Append(nameIl.Create(OpCodes.Brfalse, returnValue));
    nameIl.Append(nameIl.Create(OpCodes.Ldloc, result));
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, stringLength));
    nameIl.Append(nameIl.Create(OpCodes.Ldarg_2));
    nameIl.Append(nameIl.Create(OpCodes.Ble, returnValue));
    nameIl.Append(nameIl.Create(OpCodes.Ldloc, result));
    nameIl.Append(nameIl.Create(OpCodes.Ldc_I4_0));
    nameIl.Append(nameIl.Create(OpCodes.Ldarg_2));
    nameIl.Append(nameIl.Create(OpCodes.Callvirt, substring));
    nameIl.Append(nameIl.Create(OpCodes.Stloc, result));

    nameIl.Append(returnValue);
    nameIl.Append(nameIl.Create(OpCodes.Ret));
    return (begin, end, editName);
}

static void InjectScaleCalls(MethodDefinition method, MethodDefinition begin, MethodDefinition end)
{
    var il = method.Body.GetILProcessor();
    il.InsertBefore(method.Body.Instructions[0], il.Create(OpCodes.Call, begin));
    foreach (var instruction in method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToList())
    {
        var restore = il.Create(OpCodes.Call, end);
        il.InsertBefore(instruction, restore);
        foreach (var branch in method.Body.Instructions)
        {
            if (branch.Operand == instruction)
                branch.Operand = restore;
            else if (branch.Operand is Instruction[] targets)
            {
                for (var index = 0; index < targets.Length; index++)
                    if (targets[index] == instruction)
                        targets[index] = restore;
            }
        }
    }
}

static void ReplaceNameTextField(TypeDefinition panel, MethodDefinition onGui, MethodDefinition editName)
{
    var textField = onGui.Body.Instructions.SingleOrDefault(instruction =>
        instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference method
        && method.Name == "TextField" && method.DeclaringType.Name == "GUI"
        && method.Parameters.Count == 4
        && method.Parameters[0].ParameterType.FullName == "UnityEngine.Rect"
        && method.Parameters[1].ParameterType.FullName == "System.String"
        && method.Parameters[2].ParameterType.FullName == "System.Int32"
        && method.Parameters[3].ParameterType.FullName == "UnityEngine.GUIStyle");
    if (textField is null)
        throw new InvalidOperationException("Could not locate the Android character name text field.");

    // Replace, rather than overlay, the IMGUI TextField. Login uses this same
    // textField-style GUI.Button pattern to receive Android touches and launch IME.
    textField.Operand = panel.Module.ImportReference(editName);
}

static void PersistRememberedLogin(ModuleDefinition module, ModuleDefinition unity, MethodDefinition login)
{
    var playerPrefs = unity.GetType("UnityEngine.PlayerPrefs")
        ?? throw new InvalidOperationException("Could not find UnityEngine.PlayerPrefs.");
    var save = module.ImportReference(playerPrefs.Methods.Single(method => method.Name == "Save"
        && method.IsStatic && method.Parameters.Count == 0));
    var rememberWritesComplete = login.Body.Instructions.SingleOrDefault(instruction =>
        instruction.OpCode == OpCodes.Stfld
        && instruction.Operand is FieldReference field
        && field.Name == "_errorAlpha"
        && field.DeclaringType.Name == "LoginPanelGUI");
    if (rememberWritesComplete is null)
        throw new InvalidOperationException("Could not locate the point after Remember Me credentials are written.");

    // Login writes Player_AutoLogin, and when checked, Player_Email/Player_Password.
    // Flush those values before validation or a network request can leave the panel.
    login.Body.GetILProcessor().InsertAfter(rememberWritesComplete, Instruction.Create(OpCodes.Call, save));
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes))
            yield return nested;
    }
}
