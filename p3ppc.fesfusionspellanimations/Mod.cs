using System.Drawing;
using CriFs.V2.Hook.Interfaces;
using p3ppc.fesfusionspellanimations.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace p3ppc.fesfusionspellanimations
{
    public unsafe class Mod : ModBase
    {
        // Fusion spells use logical motions 23 (shoot), 24 and 25 (the two fusion poses).
        // P3P plays them with the summon and its one fusion motion, FES had its own three.
        private const ushort FusionShoot = 23;
        private const ushort FusionB = 25;
        private const short FirstNewSlot = 24;

        private readonly ILogger _logger = null!;
        private IHook<MapMotion> _mapHook = null!;
        private IHook<StartMotion> _startHook = null!;
        private HasMotion _hasMotion = null!;
        private IsFemc _isFemc = null!;
        private short _pendingSlot = -1;
        private short _fallbackSlot;

        public Mod(ModContext context)
        {
            _logger = context.Logger;
            var loader = context.ModLoader;
            var hooks = context.Hooks!;

            var controller = loader.GetController<ICriFsRedirectorApi>();
            if (controller == null || !controller.TryGetTarget(out var criFs) || criFs == null)
            {
                Log("CRI FS Hook isn't available.", Color.Red);
                return;
            }
            var packs = new FusionPacks(criFs, loader.GetDirectoryForModId(context.ModConfig.ModId), m => Log(m, Color.LightBlue));
            criFs.AddBindCallback(packs.OnBind);

            loader.GetController<IStartupScanner>().TryGetTarget(out var scanner);
            nint baseAddress = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            void Scan(string pattern, string name, Action<nint> found) => scanner!.AddMainModuleScan(pattern, r =>
            {
                if (r.Found) found(baseAddress + r.Offset);
                else Log($"Couldn't find {name}.", Color.Red);
            });

            Scan("48 89 5C 24 ?? 57 48 83 EC 20 48 89 CB 0F B7 FA 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 44 0F B6 83", "the motion mapper",
                a => _mapHook = hooks.CreateHook<MapMotion>(OnMapMotion, a).Activate());
            Scan("48 83 EC 28 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 05 ?? ?? ?? ?? C1 E8 07", "the route check",
                a => _isFemc = hooks.CreateWrapper<IsFemc>(a, out _));
            Scan("E8 ?? ?? ?? ?? B8 ?? ?? ?? ?? 41 89 06 E8 ?? ?? ?? ?? 89 B0 A0 13 00 00", "StartMotion", a =>
            {
                nint start = Call(a);
                _hasMotion = hooks.CreateWrapper<HasMotion>(Call(start + 0x68), out _);
                _startHook = hooks.CreateHook<StartMotion>(OnStartMotion, start).Activate();
            });
        }

        // The game keeps getting P3P's slots so its timing table (event frames, speed, blend) still applies,
        // the FES motion is swapped in when that slot actually starts
        private ulong OnMapMotion(nuint actor, ushort logical)
        {
            ulong slot = _mapHook.OriginalFunction(actor, logical);
            if (!IsProtagonist(actor))
                return slot;

            _pendingSlot = -1;
            if (logical < FusionShoot || logical > FusionB)
                return slot;

            // Kotone keeps her own P3P fusion pose
            bool kotone = _isFemc != null && _isFemc() != 0;
            if (kotone && logical != FusionShoot)
                return slot;

            _fallbackSlot = (short)slot;
            _pendingSlot = (short)(FirstNewSlot + logical - FusionShoot);
            return slot;
        }

        // Models without the new motions (a GMO swapped in by the PAK emulator) keep the P3P one
        private nuint OnStartMotion(nuint model, ushort layer, short motion, ushort transition, ushort flags)
        {
            if (_pendingSlot < 0 || motion != _fallbackSlot || _hasMotion(model, (ushort)_pendingSlot) != 1)
                return _startHook.OriginalFunction(model, layer, motion, transition, flags);

            motion = _pendingSlot;
            _pendingSlot = -1;
            var result = _startHook.OriginalFunction(model, layer, motion, transition, flags);
            HideWeapons(model);
            return result;
        }

        // The game hides attached weapons for slots 11-14 and 21 only, the FES slots need the same
        private static void HideWeapons(nuint model)
        {
            for (int i = 0; i < 5; i++)
            {
                nuint entry = model + 0xB28 + (nuint)(i * 0x18);
                nuint attached = *(nuint*)entry;
                byte kind = *(byte*)(entry + 0xC);
                if ((*(byte*)(entry - 8) & 1) == 0 || attached == 0 || kind == 0 || kind == 3)
                    continue;
                *(uint*)(attached + 0xE8) |= 2;
            }
        }

        private static bool IsProtagonist(nuint actor)
        {
            if (actor == 0 || *(byte*)(actor + 0xA2) != 0)
                return false;
            nuint info = *(nuint*)(actor + 0xCE0);
            return info != 0 && *(ushort*)(info + 2) == 1;
        }

        private static nint Call(nint instruction) => instruction + 5 + *(int*)(instruction + 1);

        private void Log(string message, Color color) => _logger.WriteLine($"[FES Fusion] {message}", color);

        [Function(CallingConventions.Microsoft)]
        private delegate ulong MapMotion(nuint actor, ushort logical);

        [Function(CallingConventions.Microsoft)]
        private delegate nuint StartMotion(nuint model, ushort layer, short motion, ushort transition, ushort flags);

        [Function(CallingConventions.Microsoft)]
        private delegate int HasMotion(nuint model, ushort motion);

        [Function(CallingConventions.Microsoft)]
        private delegate byte IsFemc();

#pragma warning disable CS8618
        public Mod() { }
#pragma warning restore CS8618
    }
}
