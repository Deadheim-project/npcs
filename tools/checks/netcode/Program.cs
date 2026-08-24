using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Confirms the IL that Patches/NetworkTuningPatches.cs rewrites still looks the way the
// patches expect. A Harmony transpiler that matches nothing throws no error -- it hands the
// original IL back and the server quietly runs vanilla send rates. Catching that here means
// it surfaces on a build instead of as "the lag came back" three weeks after a game update.
class Program
{
    static int failed, passed;

    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { passed++; Console.WriteLine("  PASS  " + what); }
        else { failed++; Console.WriteLine("  FAIL  " + what + (detail.Length > 0 ? "  -- " + detail : "")); }
    }

    static int Main(string[] args)
    {
        string managed = Environment.GetEnvironmentVariable("VALHEIM_MANAGED");
        if (string.IsNullOrEmpty(managed))
            managed = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed";

        string dll = Path.Combine(managed, "assembly_valheim.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("  SKIP  assembly_valheim.dll not found at " + managed);
            Console.WriteLine("        set VALHEIM_MANAGED to check the netcode anchors");
            return 0;
        }

        Console.WriteLine("== ZDOMan send-path anchors ==");

        using (var asm = AssemblyDefinition.ReadAssembly(dll))
        {
            var zdoMan = asm.MainModule.GetType("ZDOMan");
            if (zdoMan == null) { Check("ZDOMan exists", false); return Report(); }

            // 1. The per-peer byte budget, replaced by ZDOMan_SendZDOs_Patch. Both sites must
            //    move together: one is the "queue already full" test, the other produces the
            //    remaining headroom, and changing only one would let it write past the cap.
            var sendZdos = Body(zdoMan, "SendZDOs");
            int budgetSites = sendZdos == null ? -1 : sendZdos.Instructions
                .Count(i => i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == 10240);
            Check("SendZDOs has 2 x ldc.i4 10240", budgetSites == 2, "found " + budgetSites);

            // 2. The distant-object gate, replaced by ZDOMan_CreateSyncList_Patch. The patch
            //    anchors on the get_Count immediately before it, so assert that shape too and
            //    not merely that some 10 exists somewhere in the method.
            var syncList = Body(zdoMan, "CreateSyncList");
            int gateSites = 0;
            if (syncList != null)
            {
                var instructions = syncList.Instructions;
                for (int i = 1; i < instructions.Count; i++)
                {
                    bool isGate = instructions[i].OpCode == OpCodes.Ldc_I4_S
                                  && Convert.ToInt32(instructions[i].Operand) == 10;
                    bool afterCount = instructions[i - 1].OpCode == OpCodes.Callvirt
                                      && instructions[i - 1].Operand is MethodReference m
                                      && m.Name == "get_Count";
                    if (isGate && afterCount) gateSites++;
                }
            }
            Check("CreateSyncList has 1 x ldc.i4.s 10 after get_Count", gateSites == 1, "found " + gateSites);

            // 3. We deliberately leave SendZDOToPeers2 to VBNetTweaks, which swaps the whole
            //    method for its own peer loop. That loop calls the stock SendZDOs, which is
            //    what carries our budget change -- so if it ever stopped calling it, our
            //    transpiler would still apply and still do nothing. Assert the call is there.
            var toPeers = Body(zdoMan, "SendZDOToPeers2");
            Check("SendZDOToPeers2 still calls SendZDOs", Calls(toPeers, "SendZDOs"));
        }

        return Report();
    }

    static MethodBody Body(TypeDefinition type, string name)
    {
        var method = type.Methods.FirstOrDefault(m => m.Name == name && m.HasBody);
        return method?.Body;
    }

    static bool Touches(MethodBody body, string field) =>
        body != null && body.Instructions.Any(i =>
            i.Operand is FieldReference f && f.Name == field);

    static bool Calls(MethodBody body, string method) =>
        body != null && body.Instructions.Any(i =>
            i.Operand is MethodReference m && m.Name == method);

    static int Report()
    {
        Console.WriteLine();
        Console.WriteLine("  " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
