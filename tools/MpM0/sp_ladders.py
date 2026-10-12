"""Isolated API 1.9.3.1 ladder probe (airdrop-ladder-hud-smoke-20261012: "探员和匪好像都不会爬梯子").

In a new Survival world's cleared arena: a platform (top at y+4) with a four-cell wooden ladder on its west face. A CT
companion follows its owner up the ladder and back down; then a hostile T (close-quarters role, alerted to the owner on the
platform beyond its 9 m stand-off) chases up the same ladder. Positions are sampled to show no teleporting. No original
worlds or Mods writes. Usage (M0_ENGINE=131): python sp_ladders.py <label> <full-package>
"""
import json, sys, time
from pathlib import Path
import m0
from m0 import poll, to_menu, sha, RESULTS, RUNS
from mp_m1 import MAIN, PROJECT, KEEP_ACTIVE, SURVIVAL, build_arena, teleport, STAND

def body_of(eid): return PROJECT + (f'var e = project.Entities.FirstOrDefault(q => q.Id == {eid}); var b = e?.FindComponent<Game.ComponentBody>(); if (b == null) return "gone"; '
    'var l = e.FindComponent<Game.ComponentLocomotion>(); var c = e.FindComponent<Game.ComponentTacticalCompanion>(); '
    'return System.FormattableString.Invariant($"{b.Position.X} {b.Position.Y} {b.Position.Z} {(b.StandingOnValue.HasValue ? 1 : 0)} {(l?.LadderValue.HasValue == true ? 1 : 0)} {c?.Status ?? "-"}");')

def main(label, package):
    case = RUNS / f'ladders-{label}-{time.strftime("%Y%m%d-%H%M%S")}'
    case.mkdir(parents=True, exist_ok=True); g = None
    result = {'package': str(package), 'sha256': sha(package), 'checks': [], 'tracks': {}}
    def check(name, ok, detail=None):
        result['checks'].append(dict(name=name, ok=bool(ok), detail=detail)); print(('PASS ' if ok else 'FAIL ') + name, flush=True)
        if not ok: raise AssertionError(name + ': ' + str(detail))
    def stand_at(px, py, pz):
        g.func(teleport(px, py, pz)); g.func(STAND); time.sleep(.6)
    def track(name, eid, until, seconds):
        """Samples the agent until until(sample) holds or seconds pass; returns the samples."""
        samples = []; t0 = time.time()
        while time.time() - t0 < seconds:
            r = g.func(body_of(eid))
            if r == 'gone': break
            x_, y_, z_, standing, ladder, status = r.split(' ', 5)
            s = dict(t=round(time.time() - t0, 2), x=float(x_), y=float(y_), z=float(z_), standing=standing == '1', ladder=ladder == '1', status=status)
            samples.append(s)
            if until(s): break
            time.sleep(.2)
        result['tracks'][name] = samples; return samples
    def continuous(samples):
        """Largest move between two consecutive samples (m) and the rate it implies (m/s)."""
        worst = 0.0
        for a, b in zip(samples, samples[1:]):
            d = ((a['x'] - b['x']) ** 2 + (a['y'] - b['y']) ** 2 + (a['z'] - b['z']) ** 2) ** .5
            worst = max(worst, d / max(.05, b['t'] - a['t']))
        return worst
    try:
        g = m0.game('server', case, [Path(package)]); result['engine'] = g.engine_info(); to_menu(g)
        mark = g.mark(); g.cmd('CLICK_WIDGET Play'); g.wait('Entered screen "Play"', 60, mark)
        mark = g.mark(); g.cmd('CLICK_WIDGET NewWorld'); g.wait('Entered screen "NewWorld"', 60, mark)
        poll(g, SURVIVAL, lambda x: x == 'Survival', 20)
        mark = g.mark(); g.cmd('CLICK_WIDGET Play'); g.wait('Entered screen "Player"', 120, mark)
        mark = g.mark(); g.cmd('CLICK_WIDGET PlayButton'); g.wait('Entered screen "Game"', 180, mark)
        poll(g, m0.PLAYER_SPAWNED, lambda x: x == 'True', 120); g.func(KEEP_ACTIVE)
        # The owner is a fixture for the agents to reach; the hostile's shots must not end the run.
        g.func(MAIN + 'pl.ComponentHealth.AttackResilience = 10000; return "durable owner";')
        x, y, z = map(int, build_arena(g).split())
        # Platform top at y+4 over x+1..x+4, z+8..z+12, its west face a wall from the floor; a wooden ladder on that face
        # (placed on face 3, so it hangs on the +X wall) from y to y+3.
        built = g.func(PROJECT + (
            'var t = project.FindSubsystem<Game.SubsystemTerrain>(true); int g = Game.Terrain.MakeBlockValue(Game.GraniteBlock.Index); '
            f'for (int dx = 1; dx <= 4; dx++) for (int dz = 8; dz <= 12; dz++) t.ChangeCell({x} + dx, {y} + 3, {z} + dz, g); '
            f'for (int dy = 0; dy <= 2; dy++) t.ChangeCell({x} + 1, {y} + dy, {z} + 10, g); '
            'int ladder = Game.Terrain.MakeBlockValue(Game.BlocksManager.GetBlockIndex<Game.WoodenLadderBlock>(true), 0, Game.LadderBlock.SetFace(0, 3)); '
            f'for (int dy = 0; dy <= 3; dy++) t.ChangeCell({x}, {y} + dy, {z} + 10, ladder); '
            f'return Game.Terrain.ExtractContents(t.Terrain.GetCellValue({x}, {y} + 3, {z} + 10)) + " " + Game.Terrain.ExtractContents(t.Terrain.GetCellValue({x} + 1, {y} + 3, {z} + 10));'))
        result['structure'] = dict(arena=[x, y, z], ladder=[x, z + 10, y, y + 3], built=built)
        g.func(MAIN + 'foreach (var e in project.Entities.ToArray()) if (e.FindComponent<Game.ComponentCreature>() != null && e.FindComponent<Game.ComponentPlayer>() == null) project.RemoveEntity(e, true); return "empty arena";')
        stand_at(x - 2, y, z + 4)
        eid = g.func(MAIN + (
            'var e = Game.DatabaseManager.CreateEntity(project, "ScTacticalCT", true); '
            f'e.FindComponent<Game.ComponentBody>(true).Position = new Engine.Vector3({x} - 1.5f, {y}, {z} + 6.5f); '
            'var c = e.FindComponent<Game.ComponentTacticalCompanion>(true); c.OwnerIndex = pl.PlayerData.PlayerIndex; '
            'var sp = e.FindComponent<Game.ComponentSpawn>(); if (sp != null) sp.SpawnDuration = 0f; project.AddEntity(e); return e.Id.ToString();'))
        time.sleep(3)
        # 1. Up: the owner stands on the platform; the companion must use the ladder to reach that floor.
        stand_at(x + 3, y + 4, z + 10)
        up = track('companion-up', eid, lambda s: s['standing'] and s['y'] >= y + 3.9, 45)
        check('companion climbs the ladder to its owner on the platform', up and up[-1]['standing'] and up[-1]['y'] >= y + 3.9, up[-1] if up else None)
        check('companion was on the ladder on the way up', any(s['ladder'] for s in up), [s for s in up if s['ladder']][:3])
        check('companion moved continuously going up (no teleport)', continuous(up) < 12, continuous(up))
        # 2. Down: the owner returns to the floor; the companion climbs back down.
        stand_at(x - 2, y, z + 4)
        down = track('companion-down', eid, lambda s: s['standing'] and s['y'] <= y + .6, 45)
        check('companion climbs back down to its owner', down and down[-1]['standing'] and down[-1]['y'] <= y + .6, down[-1] if down else None)
        check('companion was on the ladder on the way down', any(s['ladder'] for s in down), [s for s in down if s['ladder']][:3])
        check('companion moved continuously going down (no teleport)', continuous(down) < 12, continuous(down))
        g.func(PROJECT + f'var e = project.Entities.FirstOrDefault(q => q.Id == {eid}); if (e != null) project.RemoveEntity(e, true); return "companion removed";')
        # 3. A hostile chases the owner up the ladder: close-quarters role, alerted, owner beyond its 9 m stand-off.
        stand_at(x + 3, y + 4, z + 11)
        tid = g.func(MAIN + (
            'var e = Game.DatabaseManager.CreateEntity(project, "ScTacticalEnemy", true); var en = e.FindComponent<Game.ComponentTacticalEnemy>(true); '
            'var st = new Game.TacticalEnemyState { Squad = "ladder-test", Role = Game.TacticalRole.Close, Variant = System.Array.FindIndex(Game.GunSpec.All, s => s.Name == "ak47"), Rounds = 30, Reserve = 90, Source = "manual" }; '
            f'en.Configure(st, new Engine.Vector3({x} - 3.5f, {y}, {z} + 2.5f)); var sp = e.FindComponent<Game.ComponentSpawn>(); if (sp != null) sp.SpawnDuration = 0f; '
            'project.AddEntity(e); en.Alert(pl.ComponentBody); return e.Id.ToString();'))
        chase = track('hostile-up', tid, lambda s: s['standing'] and s['y'] >= y + 3.9, 45)
        check('hostile chases up the ladder onto the platform', chase and chase[-1]['standing'] and chase[-1]['y'] >= y + 3.9, chase[-1] if chase else None)
        check('hostile was on the ladder', any(s['ladder'] for s in chase), [s for s in chase if s['ladder']][:3])
        check('hostile moved continuously (no teleport)', continuous(chase) < 12, continuous(chase))
        result['gameErrors'] = g.errors(); check('no game errors', not result['gameErrors'], result['gameErrors'])
    except Exception as e:
        result['failure'] = str(e); print('FAILURE', str(e), flush=True)
        if g is not None: result['gameErrors'] = g.errors()
    finally:
        if g is not None: g.close()
        m0.RUNTIME_OWNER.release()
        out = RESULTS / f'ladders-{label}.json'; out.write_text(json.dumps(result, indent=2) + '\n', 'utf8'); print(out, flush=True)
    return 1 if result.get('failure') else 0

if __name__ == '__main__': sys.exit(main(sys.argv[1], Path(sys.argv[2])))
