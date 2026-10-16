"""Multiplayer ladder and airdrop-rules probe on 1.9.3.2_MP (airdrop-ladder-hud-smoke-20261012).

The host's CT companion follows the host up a ladder (host-authoritative movement). A joined client must see the climb
through the platform's body replication, and its copy of the agent must never play the airborne pose while on the ladder
(the client has no LadderValue; TacticalLadderClimb.OnLadder stands in). The world's airdrop rules belong to the host: a
client's write through the settings bridge is refused, the host's is taken. No original worlds or Mods writes.
Usage: python mp_ladders.py <tag> <spec>   (spec as m0.cs_packages: full | lite+agents | full/lite+agents)
"""
import json, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import m0
from m0 import Game, MODS, RESULTS, RUNS, TA_BUILT, MPBIN, sha, to_menu, join, poll, cs_packages, run_label
from mp_m1 import PROJECT, MAIN, SURVIVAL, HANDSHAKE, build_arena, teleport, STAND, EYE, near

def observe(eid): return PROJECT + (
    f'var e = project.Entities.FirstOrDefault(q => q.Id == {eid}); var b = e?.FindComponent<Game.ComponentBody>(); if (b == null) return "gone"; '
    'var m = e.FindComponent<Game.ComponentTacticalModel>(); bool ladder = Game.TacticalLadderClimb.OnLadder(project.FindSubsystem<Game.SubsystemTerrain>(true), b); '
    'return System.FormattableString.Invariant($"{b.Position.X} {b.Position.Y} {b.Position.Z} {(m != null && m.Air.Airborne ? 1 : 0)} {(ladder ? 1 : 0)}");')
RULES = PROJECT + 'var r = Game.ScAirdropRulesBridge.Read?.Invoke(project); return r.HasValue ? r.Value.Enabled + " " + r.Value.Days : "none";'
def write_rules(on, days): return PROJECT + f'return (Game.ScAirdropRulesBridge.Write?.Invoke(project, new Game.ScAirdropRules({str(on).lower()}, {days})) == true).ToString();'

def main(tag, spec):
    server_pkgs, client_pkgs = cs_packages(tag, spec); ta = MODS / TA_BUILT.name
    case_dir = RUNS / f"ladders-mp-{tag}-{run_label(spec)}-{time.strftime('%Y%m%d-%H%M%S')}"; case_dir.mkdir(parents=True)
    R = {"case": case_dir.name, "package": {p.name: sha(p) for p in {*server_pkgs, *client_pkgs}}, "mpBuild": sha(MPBIN / "Survivalcraft.dll"), "checks": [], "tracks": {}}
    def check(name, ok, detail=""):
        R["checks"].append({"check": name, "ok": bool(ok), "detail": detail}); print(("PASS " if ok else "FAIL ") + name + " :: " + str(detail)[:400], flush=True)
    server = c1 = None
    try:
        server = Game("server", case_dir, [ta, *server_pkgs]); to_menu(server)
        server.cmd('EXEC SettingsManager.ShowPlayWithFriendsDialog = false; SettingsManager.PlayWithFriendsEnabled = false; SettingsManager.SaveSettings();')
        m = server.mark(); server.cmd("CLICK_WIDGET Play"); server.wait('Entered screen "Play"', 120, m)
        m = server.mark(); server.cmd("CLICK_WIDGET NewWorld"); server.wait('Entered screen "NewWorld"', 120, m)
        poll(server, SURVIVAL, lambda v: v == "Survival", 20)
        m = server.mark(); server.cmd("CLICK_WIDGET Play"); server.wait('Entered screen "Player"', 600, m)
        m = server.mark(); server.cmd("CLICK_WIDGET PlayButton"); server.wait("Player into playing.", 600, m)
        x, y, z = map(int, build_arena(server).split())
        # The same platform and ladder as sp_ladders.py, built before the client joins (pre-join terrain reaches it whole).
        server.func(PROJECT + (
            'var t = project.FindSubsystem<Game.SubsystemTerrain>(true); int g = Game.Terrain.MakeBlockValue(Game.GraniteBlock.Index); '
            f'for (int dx = 1; dx <= 4; dx++) for (int dz = 8; dz <= 12; dz++) t.ChangeCell({x} + dx, {y} + 3, {z} + dz, g); '
            f'for (int dy = 0; dy <= 2; dy++) t.ChangeCell({x} + 1, {y} + dy, {z} + 10, g); '
            'int ladder = Game.Terrain.MakeBlockValue(Game.BlocksManager.GetBlockIndex<Game.WoodenLadderBlock>(true), 0, Game.LadderBlock.SetFace(0, 3)); '
            f'for (int dy = 0; dy <= 3; dy++) t.ChangeCell({x}, {y} + dy, {z} + 10, ladder); return "built";'))
        server.func(MAIN + 'foreach (var e in project.Entities.ToArray()) if (e.FindComponent<Game.ComponentCreature>() != null && e.FindComponent<Game.ComponentPlayer>() == null) project.RemoveEntity(e, true); return "empty arena";')
        c1 = Game("client1", case_dir, [ta, *client_pkgs]); to_menu(c1); join(c1, server)
        check("client accepted", poll(c1, HANDSHAKE, lambda v: v == "Accepted", 30) == "Accepted")
        ladder_on_client = poll(c1, PROJECT + f'return Game.TacticalLadderClimb.IsLadder(project.FindSubsystem<Game.SubsystemTerrain>(true), {x}, {y} + 3, {z} + 10).ToString();', lambda v: v == "True", 60)
        check("the client has the host's ladder", ladder_on_client == "True", ladder_on_client)
        # Host stands on the floor; client watches from the side, out of the way.
        server.func(teleport(x - 2, y, z + 4)); server.func(STAND)
        c1.func(teleport(x - 3, y, z + 12)); c1.func(STAND); time.sleep(1)
        eid = server.func(MAIN + (
            'var e = Game.DatabaseManager.CreateEntity(project, "ScTacticalCT", true); '
            f'e.FindComponent<Game.ComponentBody>(true).Position = new Engine.Vector3({x} - 1.5f, {y}, {z} + 6.5f); '
            'var c = e.FindComponent<Game.ComponentTacticalCompanion>(true); c.OwnerIndex = pl.PlayerData.PlayerIndex; '
            'var sp = e.FindComponent<Game.ComponentSpawn>(); if (sp != null) sp.SpawnDuration = 0f; project.AddEntity(e); return e.Id.ToString();'))
        check("the client has the host's companion", poll(c1, observe(eid), lambda v: v != "gone", 20) != "gone")
        time.sleep(2)
        server.func(teleport(x + 3, y + 4, z + 10)); server.func(STAND)
        samples = []; t0 = time.time()
        while time.time() - t0 < 45:
            s, c = server.func(observe(eid)), c1.func(observe(eid))
            if "gone" in (s, c): break
            sv, cv = [float(v) for v in s.split()], [float(v) for v in c.split()]
            samples.append(dict(t=round(time.time() - t0, 2), server=sv, client=cv))
            if sv[1] >= y + 3.9 and cv[1] >= y + 3.9: break
            time.sleep(.15)
        R["tracks"]["companion-up"] = samples
        top = max((s["client"][1] for s in samples), default=0)
        check("host's companion climbs onto the platform (host)", samples and samples[-1]["server"][1] >= y + 3.9, samples[-1] if samples else None)
        check("the client sees the companion climb (replicated height)", top >= y + 3.9, top)
        on_ladder = [s for s in samples if s["client"][4] == 1]
        check("the client saw the companion on the ladder", len(on_ladder) >= 3, len(on_ladder))
        check("never the airborne pose on the ladder (client)", all(s["client"][3] == 0 for s in on_ladder), [s for s in on_ladder if s["client"][3] == 1][:3])
        check("never the airborne pose on the ladder (host)", all(s["server"][3] == 0 for s in samples if s["server"][4] == 1), [s for s in samples if s["server"][4] == 1 and s["server"][3] == 1][:3])
        # Airdrop rules: the host's world owns them.
        before = server.func(RULES)
        check("a client's airdrop-rules write is refused", c1.func(write_rules(False, 5)) == "False")
        check("the host's rules are unchanged by the client", server.func(RULES) == before, [before, server.func(RULES)])
        check("the host's write is taken", server.func(write_rules(False, 5)) == "True" and server.func(RULES) == "False 5", server.func(RULES))
        errors = {g.name: g.errors() for g in [server, c1]}; R["gameErrors"] = errors
        check("no game errors", not any(errors.values()), errors)
    except Exception as e:
        R["failure"] = str(e); print("FAILURE", e, flush=True)
    finally:
        for g in [c1, server]:
            if g is not None: g.close()
        m0.RUNTIME_OWNER.release()
        out = RESULTS / f"ladders-mp-{tag}-{run_label(spec)}.json"; out.write_text(json.dumps(R, indent=2, ensure_ascii=False) + "\n", "utf8"); print(out, flush=True)
    return 1 if R.get("failure") or not all(c["ok"] for c in R["checks"]) else 0

if __name__ == "__main__": sys.exit(main(sys.argv[1], sys.argv[2]))
