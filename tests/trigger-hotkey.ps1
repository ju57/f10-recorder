# Exercise the installed application's registered save shortcut through the normal Windows keyboard path.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RecorderHotkeyTest {
    [StructLayout(LayoutKind.Explicit, Size=40)] public struct Input {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    public static void Press() {
        var inputs = new Input[6];
        ushort[] keys = {17,16,121,121,16,17};
        for(int i=0;i<6;i++) { inputs[i].Type=1; inputs[i].Key=keys[i]; inputs[i].Flags=i>=3 ? 2u : 0u; }
        if(SendInput(6,inputs,40)!=6) throw new System.ComponentModel.Win32Exception();
    }
}
'@
[RecorderHotkeyTest]::Press()
