using Engine;
namespace Game;

/// <summary>Ladder use for agents (airdrop-ladder-hud-smoke-20261012: "探员和匪好像都不会爬梯子"). Native pathfinding never routes
/// over a ladder, but an agent's locomotion has the vanilla ladder ability (LadderSpeed 2.5 from the Creature template): an
/// agent pushing into a ladder's slab, or off the ground inside a ladder cell, enters ladder mode, where forward walking
/// climbs and backward walking descends. One climb is planned when the goal is more than a step above or below and a
/// ladder nearby leads toward it: walk to the ladder, climb with the native ladder physics and step off at the landing (up),
/// or step into the ladder from its top and climb down (down). Never teleports or jumps; a climb that does not progress
/// gives up on its deadline, the ladder is skipped for a while and the caller's own navigation resumes.</summary>
public sealed class TacticalLadderClimb {
    public enum Phase { None, Approach, Mount, Climb, StepIn, Descend }
    public const float Speed=.7f,SearchCooldown=1.5f,FailCooldown=20,MaxLadder=48,MaxGoalReach=16;
    public Phase Stage {get;private set;}
    public bool Busy=>Stage!=Phase.None;
    public bool Up {get;private set;}
    public Point3 Column {get;private set;}
    public int Bottom {get;private set;}
    public int Top {get;private set;}
    /// <summary>Up: where the agent stands after stepping off. Down: where it stands before stepping in.</summary>
    public Point3 Landing {get;private set;}
    Point3 wall;double deadline,phaseUntil,nextSearch;
    readonly Dictionary<Point3,double> failed=[];

    public static bool IsLadder(SubsystemTerrain terrain,int x,int y,int z,out int face){
        face=0;if(y<0||y>255)return false;int value=terrain.Terrain.GetCellValue(x,y,z);
        if(BlocksManager.Blocks[Terrain.ExtractContents(value)] is not LadderBlock)return false;
        face=LadderBlock.GetFace(Terrain.ExtractData(value));return true;
    }
    public static bool IsLadder(SubsystemTerrain terrain,int x,int y,int z)=>IsLadder(terrain,x,y,z,out _);
    /// <summary>The body's feet are in a ladder cell (the cell the engine's own ladder entry reads, feet + 0.2). Positions are
    /// replicated, so this also holds on a multiplayer client, where LadderValue is not.</summary>
    public static bool OnLadder(SubsystemTerrain terrain,ComponentBody body)=>terrain is not null&&body is not null
        &&IsLadder(terrain,Terrain.ToCell(body.Position.X),Terrain.ToCell(body.Position.Y+.2f),Terrain.ToCell(body.Position.Z));
    /// <summary>No collision box other than a ladder's slab overlaps [from, to) in the column.</summary>
    static bool Clear(SubsystemTerrain terrain,int x,int z,float from,float to){
        for(int y=Terrain.ToCell(from);y<=Terrain.ToCell(to-.01f);y++){
            if(y<0||y>255)return false;
            int value=terrain.Terrain.GetCellValue(x,y,z);var block=BlocksManager.Blocks[Terrain.ExtractContents(value)];
            if(block is LadderBlock||!block.IsCollidable_(value))continue;
            foreach(var box in block.GetCustomCollisionBoxes(terrain,value))if(y+box.Max.Y>from&&y+box.Min.Y<to)return false;
        }
        return true;
    }
    /// <summary>Feet at height <paramref name="y"/> on a full, safe block below, with room for the body above.</summary>
    public static bool Standable(SubsystemTerrain terrain,int x,int y,int z,float height){
        if(y<1||y>254||terrain.Terrain.GetChunkAtCell(x,z) is not {State:>=TerrainChunkState.InvalidLight})return false;
        int below=terrain.Terrain.GetCellValue(x,y-1,z);var block=BlocksManager.Blocks[Terrain.ExtractContents(below)];
        if(block is LadderBlock or FluidBlock||!block.IsCollidable_(below)||block.ShouldAvoid(below))return false;
        var boxes=block.GetCustomCollisionBoxes(terrain,below);
        return boxes.Length>0&&boxes.Max(b=>b.Max.Y)>=.9f&&Clear(terrain,x,z,y+.02f,y+height);
    }
    /// <summary>Extent of the ladder column through (x, y, z): its lowest and highest ladder cells.</summary>
    static (int Bottom,int Top) Extent(SubsystemTerrain terrain,int x,int y,int z){
        int bottom=y,top=y;
        while(bottom>1&&y-bottom<MaxLadder&&IsLadder(terrain,x,bottom-1,z))bottom--;
        while(top<254&&top-y<MaxLadder&&IsLadder(terrain,x,top+1,z))top++;
        return (bottom,top);
    }
    /// <summary>The cell side a ladder hangs on (its wall), from the ladder's placement face.</summary>
    static Point3 WallSide(int face)=>-CellFace.FaceToPoint3(face);
    static IEnumerable<Point3> Sides(Point3 wallSide)=>new[]{wallSide,new Point3(wallSide.Z,0,-wallSide.X),new Point3(-wallSide.Z,0,wallSide.X),-wallSide};

    /// <summary>A climb for the agent standing at <paramref name="feet"/> toward <paramref name="goal"/>, through the ladder
    /// column (x, z): up from the agent's level to a standable landing beside its top, or down from a standable place beside its
    /// top to its standable bottom. The ladder's far end must come closer to the goal than the agent is, and lie within
    /// <see cref="MaxGoalReach"/> m of it horizontally: a ladder somewhere else is never a detour to an unrelated height.</summary>
    public static (Point3 Landing,int Bottom,int Top,Point3 Wall)? Plan(SubsystemTerrain terrain,int x,int z,Vector3 feet,Vector3 goal,float height){
        int level=Terrain.ToCell(feet.Y+.2f);bool up=goal.Y>feet.Y;float now=Vector3.Distance(feet,goal);
        if(up){
            if(!IsLadder(terrain,x,level,z,out int face))return null;
            var (bottom,top)=Extent(terrain,x,level,z);var side=WallSide(face);
            if(top<level+1||top+1<goal.Y-TacticalNavigation.MaxStep)return null;
            foreach(var s in Sides(side)){
                var land=new Point3(x+s.X,top+1,z+s.Z);
                if(!Standable(terrain,land.X,land.Y,land.Z,height))continue;
                if(!Closer(new Vector3(land.X+.5f,land.Y,land.Z+.5f),goal,now))continue;
                return (land,bottom,top,side);
            }
            return null;
        }
        // Down: the ladder reaches the agent's level (its top cell at the level, or just below it under open air).
        int topCell=IsLadder(terrain,x,level,z)?level:IsLadder(terrain,x,level-1,z)&&Clear(terrain,x,z,level+.02f,level+height)?level-1:-1;
        if(topCell<0||!IsLadder(terrain,x,topCell,z,out int f))return null;
        var (low,high)=Extent(terrain,x,topCell,z);var wallSide=WallSide(f);
        if(level-low<2||low>goal.Y+TacticalNavigation.MaxStep||!Standable(terrain,x,low,z,height))return null;
        if(!Closer(new Vector3(x+.5f,low,z+.5f),goal,now))return null;
        foreach(var s in Sides(wallSide)){
            var stand=new Point3(x+s.X,level,z+s.Z);
            if(Standable(terrain,stand.X,stand.Y,stand.Z,height))return (stand,low,high,wallSide);
        }
        return null;
    }

    static bool Closer(Vector3 end,Vector3 goal,float now)=>Vector3.Distance(end,goal)<now-1&&Vector2.Distance(end.XZ,goal.XZ)<=MaxGoalReach;
    /// <summary>Starts a climb when the goal is more than a step above or below and a ladder within <paramref name="radius"/>
    /// cells leads toward it (searched at most every <see cref="SearchCooldown"/> s; a failed ladder is skipped for
    /// <see cref="FailCooldown"/> s). True while a climb is active.</summary>
    public bool TryStart(ComponentCreature creature,SubsystemTerrain terrain,ComponentPathfinding path,Vector3 goal,int radius,double now){
        if(Busy)return true;
        var body=creature?.ComponentBody;
        if(body is null||terrain is null||!(creature.ComponentLocomotion?.LadderSpeed>0)||Math.Abs(goal.Y-body.Position.Y)<=TacticalNavigation.MaxStep||now<nextSearch)return false;
        nextSearch=now+SearchCooldown;
        int fx=Terrain.ToCell(body.Position.X),fz=Terrain.ToCell(body.Position.Z);float best=float.MaxValue;
        (Point3 Column,Point3 Landing,int Bottom,int Top,Point3 Wall)? chosen=null;
        for(int x=fx-radius;x<=fx+radius;x++)for(int z=fz-radius;z<=fz+radius;z++){
            if(failed.TryGetValue(new Point3(x,0,z),out double until)&&now<until)continue;
            if(terrain.Terrain.GetChunkAtCell(x,z) is not {State:>=TerrainChunkState.InvalidLight})continue;
            if(Plan(terrain,x,z,body.Position,goal,body.BoxSize.Y) is not {} plan)continue;
            var center=new Vector2(x+.5f,z+.5f);
            float score=Vector2.Distance(body.Position.XZ,center)+.5f*Vector2.Distance(goal.XZ,center);
            if(score<best){best=score;chosen=(new Point3(x,0,z),plan.Landing,plan.Bottom,plan.Top,plan.Wall);}
        }
        if(chosen is not {} c)return false;
        Up=goal.Y>body.Position.Y;Column=c.Column;Landing=c.Landing;Bottom=c.Bottom;Top=c.Top;wall=c.Wall;
        Stage=Phase.Approach;deadline=now+12+(Top-Bottom+1)/1.2f;phaseUntil=deadline;path.Stop();
        Log.Information($"[CS_LADDER] {creature.Entity?.ValuesDictionary?.DatabaseObject?.Name} {(Up?"up":"down")} column=({Column.X},{Column.Z}) {Bottom}..{Top} landing=({Landing.X},{Landing.Y},{Landing.Z})");
        return true;
    }
    public void Cancel(){Stage=Phase.None;}
    void Fail(ComponentCreature creature,ComponentPathfinding path,double now,string why){
        failed[Column]=now+FailCooldown;Stage=Phase.None;path.Stop();
        Log.Information($"[CS_LADDER] {creature.Entity?.ValuesDictionary?.DatabaseObject?.Name} gave up ({why}) column=({Column.X},{Column.Z})");
    }
    static void Hold(ComponentPathfinding path){
        path.Stop();var pilot=path.m_componentPilot;pilot.m_walkOrder=null;pilot.m_turnOrder=Vector2.Zero;pilot.m_jumpOrder=0;
    }
    static Vector2 TurnToward(ComponentBody body,Vector2 target){
        var d=target-body.Position.XZ;return d.LengthSquared()<.0001f?Vector2.Zero:new Vector2(Math.Clamp(Vector2.Angle(body.Matrix.Forward.XZ,d)*1.5f,-1,1),0);
    }

    /// <summary>One frame of an active climb. True while the climb owns the agent's movement (the caller skips its own
    /// navigation and step jumps); false once it ended, arrived or gave up.</summary>
    public bool Update(ComponentCreature creature,SubsystemTerrain terrain,ComponentPathfinding path,double now){
        if(!Busy)return false;
        var body=creature.ComponentBody;var loco=creature.ComponentLocomotion;
        if(creature.ComponentHealth.Health<=0||body.ParentBody is not null){Cancel();return false;}
        if(now>deadline){Fail(creature,path,now,"deadline");return false;}
        bool inColumn=Terrain.ToCell(body.Position.X)==Column.X&&Terrain.ToCell(body.Position.Z)==Column.Z;
        bool onLadder=loco.LadderValue.HasValue||OnLadder(terrain,body)&&!body.StandingOnValue.HasValue;
        var center=new Vector3(Column.X+.5f,body.Position.Y,Column.Z+.5f);
        var landing=new Vector3(Landing.X+.5f,Landing.Y,Landing.Z+.5f);
        switch(Stage){
            case Phase.Approach:{
                // Up: into the ladder column at the agent's level. Down: onto the place beside the ladder's top.
                var target=Up?center:landing;
                if(Up?inColumn:Vector2.DistanceSquared(body.Position.XZ,target.XZ)<.35f*.35f&&Math.Abs(body.Position.Y-target.Y)<.6f){
                    Stage=Up?Phase.Mount:Phase.StepIn;phaseUntil=now+3;Hold(path);return true;
                }
                if(path.IsStuck){Fail(creature,path,now,"approach stuck");return false;}
                TacticalNavigation.Navigate(path,target,Speed,.25f,250,false,false,false,null,.3f);
                return true;
            }
            case Phase.Mount:{
                // Face the wall and walk into the slab: the engine's own ladder entry (the same as a player's).
                if(onLadder){Stage=Phase.Climb;return true;}
                if(!inColumn&&body.StandingOnValue.HasValue||now>phaseUntil){Fail(creature,path,now,"mount");return false;}
                Hold(path);var w=new Vector2(wall.X,wall.Z);
                loco.TurnOrder=TurnToward(body,body.Position.XZ+w);loco.WalkOrder=new Vector2(0,Speed*.6f);
                return true;
            }
            case Phase.Climb:{
                // Forward walking climbs. The goal lies 1.5 m out over the landing: the pilot keeps walking forward (never
                // within the 1 m where it would jump at random), and steps the agent onto the landing over the top.
                if(body.StandingOnValue.HasValue&&body.Position.Y>=Top+.9f){Stage=Phase.None;path.Stop();return false;}
                if(!onLadder&&body.StandingOnValue.HasValue&&body.Position.Y<Top+.5f){
                    if(!inColumn){Fail(creature,path,now,"left the ladder");return false;}
                    Stage=Phase.Mount;phaseUntil=now+2;return true; // slipped off at the foot: mount again
                }
                var out_=landing-new Vector3(Column.X+.5f,0,Column.Z+.5f);out_.Y=0;
                var target=new Vector3(Column.X+.5f,Landing.Y,Column.Z+.5f)+(out_.LengthSquared()>0?Vector3.Normalize(out_):Vector3.Zero)*1.5f;
                TacticalNavigation.Navigate(path,target,Speed,.2f,0,false,false,false,null,.3f);
                return true;
            }
            case Phase.StepIn:{
                // Walk from the top into the open column: off the ground inside the ladder cell, the engine enters ladder mode.
                if(onLadder){Stage=Phase.Descend;return true;}
                if(now>phaseUntil){Fail(creature,path,now,"step in");return false;}
                Hold(path);loco.TurnOrder=TurnToward(body,center.XZ);
                loco.WalkOrder=new Vector2(0,Vector2.DistanceSquared(body.Position.XZ,center.XZ)>.04f?Speed*.5f:0);
                return true;
            }
            case Phase.Descend:{
                // Backward walking descends; the engine holds the agent on the column centre and ends ladder mode on the floor.
                if(body.StandingOnValue.HasValue&&body.Position.Y<=Bottom+.6f){Stage=Phase.None;path.Stop();return false;}
                if(!inColumn&&!onLadder){Fail(creature,path,now,"left the ladder");return false;}
                Hold(path);loco.WalkOrder=new Vector2(0,-Speed);
                return true;
            }
        }
        return false;
    }
}
