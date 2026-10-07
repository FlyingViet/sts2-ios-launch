// IL patches applied to GodotSharp.dll / sts2.dll before the iOS NativeAOT build.
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) { Console.Error.WriteLine("usage: patcher <in GodotSharp.dll|sts2.dll> <out dll>"); return 1; }
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
var module = asm.MainModule;
int patched = 0;

MethodDefinition Method(string typeName, string methodName)
{
	var type = module.GetType(typeName) ?? throw new Exception($"type {typeName} not found");
	return type.Methods.Single(m => m.Name == methodName && m.HasBody);
}

void Stub(string typeName, string methodName, Action<ILProcessor> emit)
{
	var type = module.GetType(typeName) ?? throw new Exception($"type {typeName} not found");
	var methods = type.Methods.Where(m => m.Name == methodName && m.HasBody).ToList();
	if (methods.Count == 0) throw new Exception($"{typeName}::{methodName} not found");
	foreach (var m in methods)
	{
		m.Body = new MethodBody(m);
		emit(m.Body.GetILProcessor());
		patched++;
		Console.WriteLine($"patched {m.FullName}");
	}
}

// Copies a static method (no exception handlers) under a new name in the same type.
MethodDefinition CloneStatic(MethodDefinition m, string name)
{
	if (!m.IsStatic || m.Body.HasExceptionHandlers) throw new Exception($"can't clone {m.FullName}");
	var c = new MethodDefinition(name, m.Attributes, m.ReturnType);
	foreach (var p in m.Parameters) c.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
	foreach (var v in m.Body.Variables) c.Body.Variables.Add(new VariableDefinition(v.VariableType));
	c.Body.InitLocals = m.Body.InitLocals;
	var map = new Dictionary<Instruction, Instruction>();
	foreach (var ins in m.Body.Instructions)
	{
		var n = Instruction.Create(OpCodes.Nop);
		n.OpCode = ins.OpCode;
		n.Operand = ins.Operand switch
		{
			ParameterDefinition pd => c.Parameters[pd.Index],
			VariableDefinition vd => c.Body.Variables[vd.Index],
			_ => ins.Operand,
		};
		map[ins] = n;
		c.Body.Instructions.Add(n);
	}
	foreach (var n in c.Body.Instructions)
	{
		if (n.Operand is Instruction target) n.Operand = map[target];
		else if (n.Operand is Instruction[] targets) n.Operand = targets.Select(t => map[t]).ToArray();
	}
	m.DeclaringType.Methods.Add(c);
	return c;
}

if (asm.Name.Name == "GodotSharp")
{
	// Every engine warning/error captures a C# backtrace (very slow under NativeAOT) -> skip it.
	Stub("Godot.DebuggingUtils", "GetCurrentStackInfo", il => il.Emit(OpCodes.Ret));
	// iOS has no mouse cursor: these only produce "not supported" warnings, so make them no-ops.
	Stub("Godot.Input", "SetMouseMode", il => il.Emit(OpCodes.Ret));
	Stub("Godot.Input", "GetMouseMode", il => { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); }); // Visible
	Stub("Godot.Input", "SetCustomMouseCursor", il => il.Emit(OpCodes.Ret));

	// Engine.MaxFps: the game's FPS limit. On iOS, Metal enforces it by holding frames back at presentation while
	// the display link keeps running the game loop at 120 Hz, which paces frames unevenly. Remember what the game
	// asks for and let the port apply it (src/native/Port/PortFrameRate.cs: display link rate instead).
	{
		var engine = module.GetType("Godot.Engine") ?? throw new Exception("type Godot.Engine not found");
		var corlib = module.TypeSystem.CoreLibrary;
		var actionOpen = new TypeReference("System", "Action`1", module, corlib);
		actionOpen.GenericParameters.Add(new GenericParameter(actionOpen));
		var actionInt = new GenericInstanceType(actionOpen);
		actionInt.GenericArguments.Add(module.TypeSystem.Int32);
		var invoke = new MethodReference("Invoke", module.TypeSystem.Void, actionInt) { HasThis = true };
		invoke.Parameters.Add(new ParameterDefinition(actionOpen.GenericParameters[0]));
		FieldDefinition Field(string name, TypeReference type)
		{
			var f = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static, type);
			engine.Fields.Add(f);
			return f;
		}
		var hook = Field("PortMaxFpsHook", actionInt);
		var requested = Field("PortRequestedMaxFps", module.TypeSystem.Int32);
		var hasRequest = Field("PortHasMaxFpsRequest", module.TypeSystem.Boolean);
		var setNative = CloneStatic(Method("Godot.Engine", "SetMaxFps"), "PortSetMaxFpsNative");
		var getNative = CloneStatic(Method("Godot.Engine", "GetMaxFps"), "PortGetMaxFpsNative");

		var set = Method("Godot.Engine", "SetMaxFps");
		set.Body = new MethodBody(set);
		var il = set.Body.GetILProcessor();
		var native = il.Create(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Stsfld, requested);
		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Stsfld, hasRequest);
		il.Emit(OpCodes.Ldsfld, hook);
		il.Emit(OpCodes.Brfalse, native);
		il.Emit(OpCodes.Ldsfld, hook);
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Callvirt, invoke);
		il.Emit(OpCodes.Ret);
		il.Append(native);
		il.Emit(OpCodes.Call, setNative);
		il.Emit(OpCodes.Ret);
		patched++;
		Console.WriteLine("Engine.SetMaxFps records the request and calls PortMaxFpsHook");

		var get = Method("Godot.Engine", "GetMaxFps");
		get.Body = new MethodBody(get);
		il = get.Body.GetILProcessor();
		var fromNative = il.Create(OpCodes.Call, getNative);
		il.Emit(OpCodes.Ldsfld, hasRequest);
		il.Emit(OpCodes.Brfalse, fromNative);
		il.Emit(OpCodes.Ldsfld, requested);
		il.Emit(OpCodes.Ret);
		il.Append(fromNative);
		il.Emit(OpCodes.Ret);
		patched++;
		Console.WriteLine("Engine.GetMaxFps returns the game's requested limit");
	}
}
else if (asm.Name.Name == "sts2")
{
	// Steam Cloud saves without Steamworks: hooks that the port's own assembly (sts2native) fills in.
	var runtime = module.GetTypeReferences().First(t => t.FullName == "System.Threading.Tasks.Task").Scope;
	var taskRef = new TypeReference("System.Threading.Tasks", "Task", module, runtime);
	var saveManager = module.GetType("MegaCrit.Sts2.Core.Saves.SaveManager");

	(GenericInstanceType type, MethodReference invoke) Func(TypeReference result)
	{
		var open = new TypeReference("System", "Func`1", module, runtime);
		open.GenericParameters.Add(new GenericParameter(open));
		var closed = new GenericInstanceType(open);
		closed.GenericArguments.Add(result);
		return (closed, new MethodReference("Invoke", open.GenericParameters[0], closed) { HasThis = true });
	}

	var hooks = new TypeDefinition("MegaCrit.Sts2.Port", "PortHooks",
		TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
	module.Types.Add(hooks);
	FieldDefinition Field(string name, TypeReference type)
	{
		var f = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static, type);
		hooks.Fields.Add(f);
		return f;
	}
	var (funcSaveManager, invokeSaveManager) = Func(saveManager);
	var (funcTask, invokeTask) = Func(taskRef);
	var constructHook = Field("ConstructSaveManager", funcSaveManager);
	var cloudSyncHook = Field("CloudSync", funcTask);
	var steamId = Field("SteamId", module.TypeSystem.UInt64);
	var personaName = Field("PersonaName", module.TypeSystem.String);

	// if (hook != null) return hook();
	void Prefix(MethodDefinition m, FieldDefinition hook, MethodReference invoke)
	{
		var il = m.Body.GetILProcessor();
		var first = m.Body.Instructions[0];
		il.InsertBefore(first, il.Create(OpCodes.Ldsfld, hook));
		il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
		il.InsertBefore(first, il.Create(OpCodes.Ldsfld, hook));
		il.InsertBefore(first, il.Create(OpCodes.Callvirt, invoke));
		il.InsertBefore(first, il.Create(OpCodes.Ret));
		patched++;
		Console.WriteLine($"hooked {m.FullName}");
	}
	Prefix(Method("MegaCrit.Sts2.Core.Saves.SaveManager", "ConstructDefault"), constructHook, invokeSaveManager);
	Prefix(Method("MegaCrit.Sts2.Core.Nodes.NGame", "DoCloudSync"), cloudSyncHook, invokeTask);

	// GameStartup only waits for the cloud sync while Steamworks is initialized; always wait instead.
	var ngame = module.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
	var startup = ngame.NestedTypes.Single(t => t.Name.StartsWith("<GameStartup>d__"));
	int replaced = 0;
	foreach (var ins in startup.Methods.Single(m => m.Name == "MoveNext").Body.Instructions)
		if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "get_Initialized" && mr.DeclaringType.Name == "SteamInitializer")
		{
			ins.OpCode = OpCodes.Ldc_I4_1;
			ins.Operand = null;
			replaced++;
		}
	if (replaced != 1) throw new Exception($"expected 1 SteamInitializer.Initialized check in GameStartup, found {replaced}");
	patched++;
	Console.WriteLine("GameStartup waits for cloud sync without Steamworks");

	// Saves made on PC are tagged with the Steam platform; answer those lookups without Steamworks.
	Stub("MegaCrit.Sts2.Core.Platform.Steam.SteamPlatformUtilStrategy", "GetLocalPlayerId", il =>
	{
		il.Emit(OpCodes.Ldsfld, steamId);
		il.Emit(OpCodes.Ret);
	});
	Stub("MegaCrit.Sts2.Core.Platform.Steam.SteamPlatformUtilStrategy", "GetPlayerName", il =>
	{
		var other = il.Create(OpCodes.Ldarga_S, il.Body.Method.Parameters[0]);
		var ret = il.Create(OpCodes.Ret);
		il.Emit(OpCodes.Ldarg_1);
		il.Emit(OpCodes.Ldsfld, steamId);
		il.Emit(OpCodes.Bne_Un, other);
		il.Emit(OpCodes.Ldsfld, personaName);
		il.Emit(OpCodes.Dup);
		il.Emit(OpCodes.Brtrue, ret);
		il.Emit(OpCodes.Pop);
		il.Append(other);
		il.Emit(OpCodes.Call, new MethodReference("ToString", module.TypeSystem.String, module.TypeSystem.UInt64) { HasThis = true });
		il.Append(ret);
	});

	// ---- multiplayer over ENet (direct IP) without Steamworks; see aot/Port/PortMultiplayer.cs ----
	GenericInstanceType Generic(string name, params TypeReference[] args)
	{
		var open = new TypeReference("System", $"{name}`{args.Length}", module, runtime);
		foreach (var _ in args) open.GenericParameters.Add(new GenericParameter(open));
		var closed = new GenericInstanceType(open);
		foreach (var a in args) closed.GenericArguments.Add(a);
		return closed;
	}
	MethodReference Invoke(GenericInstanceType closed, bool returnsValue)
	{
		var gp = closed.ElementType.GenericParameters;
		var r = new MethodReference("Invoke", returnsValue ? gp[gp.Count - 1] : module.TypeSystem.Void, closed) { HasThis = true };
		for (int i = 0; i < gp.Count - (returnsValue ? 1 : 0); i++) r.Parameters.Add(new ParameterDefinition(gp[i]));
		return r;
	}
	// Inserts "if (hook != null) { <body> }" at the start of m; body ends in ret unless it falls through.
	void Guard(MethodDefinition m, FieldDefinition hook, params Instruction[] body)
	{
		var il = m.Body.GetILProcessor();
		var first = m.Body.Instructions[0];
		il.InsertBefore(first, il.Create(OpCodes.Ldsfld, hook));
		il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
		foreach (var ins in body) il.InsertBefore(first, ins);
		patched++;
		Console.WriteLine($"hooked {m.FullName}");
	}
	var joinScreen = module.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NJoinFriendScreen");
	var hostService = module.GetType("MegaCrit.Sts2.Core.Multiplayer.NetHostGameService");
	const string nullStrategy = "MegaCrit.Sts2.Core.Platform.Null.NullPlatformUtilStrategy";

	// Join screen without Steam: the game connects straight to 127.0.0.1; ask for the host's IP instead.
	var funcJoin = Generic("Func", joinScreen, taskRef);
	var mpJoin = Field("MpJoin", funcJoin);
	Guard(Method(joinScreen.FullName, "FastMpJoin"), mpJoin,
		Instruction.Create(OpCodes.Ldsfld, mpJoin), Instruction.Create(OpCodes.Ldarg_0),
		Instruction.Create(OpCodes.Callvirt, Invoke(funcJoin, true)), Instruction.Create(OpCodes.Ret));

	// Tell the port when this device starts hosting over ENet (shows the address to join).
	var actionHost = Generic("Action", hostService);
	var onHost = Field("OnENetHost", actionHost);
	Guard(Method(hostService.FullName, "StartENetHost"), onHost,
		Instruction.Create(OpCodes.Ldsfld, onHost), Instruction.Create(OpCodes.Ldarg_0),
		Instruction.Create(OpCodes.Callvirt, Invoke(actionHost, false)));

	// The null platform reports player id 1 (the ENet host's id) even on a joining client, so the end-of-run
	// progress update would credit the host's character to this device. Report the id this device joined with.
	var nullPlayerId = Field("NullPlayerId", module.TypeSystem.UInt64);
	Guard(Method(nullStrategy, "GetLocalPlayerId"), nullPlayerId,
		Instruction.Create(OpCodes.Ldsfld, nullPlayerId), Instruction.Create(OpCodes.Ret));
	var funcName = Generic("Func", module.TypeSystem.UInt64, module.TypeSystem.String);
	var nullName = Field("NullPlayerName", funcName);
	Guard(Method(nullStrategy, "GetPlayerName"), nullName,
		Instruction.Create(OpCodes.Ldsfld, nullName), Instruction.Create(OpCodes.Ldarg_1),
		Instruction.Create(OpCodes.Callvirt, Invoke(funcName, true)), Instruction.Create(OpCodes.Ret));

	// Save paths (user://default/<id>) keep the fixed null-platform id 1 whatever the multiplayer id is.
	var getLocal = module.GetType("MegaCrit.Sts2.Core.Platform.PlatformUtil").Methods.Single(m => m.Name == "GetLocalPlayerId");
	var savePathId = new MethodDefinition("SavePathPlayerId", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.UInt64);
	savePathId.Parameters.Add(new ParameterDefinition("platform", ParameterAttributes.None, getLocal.Parameters[0].ParameterType));
	hooks.Methods.Add(savePathId);
	{
		var il = savePathId.Body.GetILProcessor();
		var steam = il.Create(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Brtrue, steam); // PlatformType.None == 0
		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Conv_U8);
		il.Emit(OpCodes.Ret);
		il.Append(steam);
		il.Emit(OpCodes.Call, getLocal);
		il.Emit(OpCodes.Ret);
	}
	int pathCalls = 0;
	foreach (var m in module.GetType("MegaCrit.Sts2.Core.Saves.UserDataPathProvider").Methods.Where(m => m.HasBody))
		foreach (var ins in m.Body.Instructions)
			if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference r && r.Name == "GetLocalPlayerId" && r.DeclaringType.Name == "PlatformUtil")
			{
				ins.Operand = savePathId;
				pathCalls++;
			}
	if (pathCalls != 3) throw new Exception($"expected 3 GetLocalPlayerId calls in UserDataPathProvider, found {pathCalls}");
	patched++;
	Console.WriteLine("save paths pinned to null-platform id 1");

	// Network error popups offer only "Report bug" (a feedback form sent to Mega Crit) for errors like timeouts,
	// with no way to just close them. An unofficial port shouldn't file reports: always show OK.
	{
		var ready = Method("MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup", "_Ready");
		var load = ready.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "_showReportBugButton");
		var il = ready.Body.GetILProcessor();
		il.InsertAfter(load, il.Create(OpCodes.Ldc_I4_0));
		load.OpCode = OpCodes.Pop;
		load.Operand = null;
		patched++;
		Console.WriteLine("error popups always offer OK");
	}
}
else
{
	throw new Exception("unknown assembly " + asm.Name.Name);
}

asm.Write(args[1]);
Console.WriteLine($"{patched} patches -> {args[1]}");
return 0;
