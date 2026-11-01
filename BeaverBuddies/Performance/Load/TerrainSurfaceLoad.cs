// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.TerrainSystem;
using Timberborn.TerrainSystemRendering;
using Timberborn.MapIndexSystem;
using Timberborn.MapStateSystem;
using Timberborn.Common;
using UnityEngine;
namespace BeaverBuddies.Performance;

// Build each occupancy layer once; adjacent surface codes reuse its four bits.
// Native tile construction and all later terrain updates keep their order/code.
[HarmonyPatch(typeof(TerrainMeshManager), nameof(TerrainMeshManager.InitializeTerrainTiles))]
[HarmonyPriority(HarmonyLib.Priority.Last)]
internal static class TerrainSurfaceLoad
{
    private const BindingFlags All=LoadCompatibility.All;
    private static readonly MethodBase[] Protected = new[]{
        typeof(TerrainMeshManager).GetMethod("InitializeTerrainTiles",All)!,
        typeof(TerrainMeshManager).GetMethod("UpdateCodeForCoords",All)!,
        typeof(TerrainService).GetMethod("Underground",All)!,
        typeof(TerrainService).GetMethod("Contains",All,null,new[]{typeof(Vector2Int)},null)!,
        typeof(TerrainService).GetProperty("Size",All)!.GetMethod!,
        typeof(TerrainMap).GetMethod("IsTerrainVoxel",All)!,
        typeof(TerrainMap).GetMethod("Contains",All)!,
        typeof(MapSize).GetProperty("TerrainSize2D",All)!.GetMethod!,
        typeof(MapSize).GetProperty("TerrainSize",All)!.GetMethod!,
        typeof(MapIndexService).GetProperty("Stride",All)!.GetMethod!,
        typeof(MapIndexService).GetProperty("VerticalStride",All)!.GetMethod!,
        typeof(MapIndexService).GetProperty("TerrainSize",All)!.GetMethod!,
        typeof(MapIndexService).GetMethod("CoordinatesToIndex3D",All)!
    }.Concat(typeof(Sizing).GetMethods(All).Where(m=>m.Name=="SizeContains"))
        .Concat(typeof(VectorExtensions).GetMethods(All).Where(m=>m.Name=="Below"||m.Name=="XY")).ToArray();
    private static bool Reviewed()=>LoadCompatibility.Reviewed(LoadBodies.TerrainSurface);
    static bool Prepare()=>PerformanceSettings.Enabled&&Reviewed();
    private static bool Eligible(TerrainMeshManager manager)
    {
        if(!Reviewed()||!LoadCompatibility.Unmodified(Protected,PerformanceSettings.HarmonyId)||manager.GetType()!=typeof(TerrainMeshManager)||manager._terrainService.GetType()!=typeof(TerrainService))return false;
        var service=(TerrainService)manager._terrainService;
        var map=service._terrainMap;var size=service.Size;var ids=manager._mapIndexService;
        if(map.GetType()!=typeof(TerrainMap)||ids.GetType()!=typeof(MapIndexService)||map._mapSize.GetType()!=typeof(MapSize)||
            size!=map._mapSize.TerrainSize||map._mapSize.TerrainSize2D!=new Vector2Int(size.x,size.y)||size!=ids.TerrainSize||!ReferenceEquals(map._mapIndexService,ids)||
            size.x<1||size.y<1||size.z<1||size.x>256||size.y>256||size.z>256||
            ids.Stride!=size.x+2||ids.VerticalStride!=(size.x+2)*(size.y+2)||
            map._terrainVoxels==null||map._terrainVoxels.Length<ids.VerticalStride*size.z)return false;
        var codes=manager._surfaceShapeCodes;
        if(codes==null||codes.GetLength(0)!=size.x+2||codes.GetLength(1)!=size.y+2||codes.GetLength(2)!=size.z+3)return false;
        var offsets=TerrainMeshManager.NeighborOffsets;
        return offsets.Length==4&&offsets[0]==new Vector3Int(0,0,0)&&offsets[1]==new Vector3Int(0,1,0)&&
            offsets[2]==new Vector3Int(1,0,0)&&offsets[3]==new Vector3Int(1,1,0);
    }
    private static bool Prefix(TerrainMeshManager __instance)
    {
        if(!Eligible(__instance))return true;
        var size=__instance._terrainService.Size;
        var count=WorldTiling.TileCount3D(size.x,size.y,__instance._mapSize.MaxGameTerrainHeight+1);
        Fill(__instance);
        for(var z=0;z<count.z;z++)for(var y=0;y<count.y;y++)for(var x=0;x<count.x;x++)
            __instance.InstantiateTile(new Vector3Int(x,y,z));
        for(var z=0;z<count.z;z++)for(var y=0;y<count.y;y++)for(var x=0;x<count.x;x++)
            __instance.UpdateTile(new Vector3Int(x,y,z));
        return false;
    }
    private static void Fill(TerrainMeshManager manager)
    {
        var service=(TerrainService)manager._terrainService;
        FillCodes(service._terrainMap._terrainVoxels,service.Size,manager._surfaceShapeCodes);
    }
    internal static void FillCodes(bool[] voxels,Vector3Int size,byte[,,] codes)
    {
        var stride=size.x+2;var plane=stride*(size.y+2);
        var occupied=new byte[plane];var previous=new byte[plane];
        // Below the map is solid inside XY bounds; all margins are always air.
        for(var y=1;y<=size.y;y++)for(var x=1;x<=size.x;x++)occupied[y*stride+x]=1;
        for(var z=-2;z<=size.z;z++)
        {
            if(z>=0)
                for(var y=1;y<=size.y;y++)for(var x=1;x<=size.x;x++)
                    occupied[y*stride+x]=(byte)(z<size.z&&voxels[z*plane+y*stride+x]?1:0);
            for(var y=0;y<size.y+2;y++)for(var x=0;x<size.x+2;x++)
            {
                var i=y*stride+x;
                var bits=(int)occupied[i];
                if(y<=size.y)bits|=occupied[i+stride]<<2;
                if(x<=size.x)bits|=occupied[i+1]<<4;
                if(x<=size.x&&y<=size.y)bits|=occupied[i+stride+1]<<6;
                var below=z==-2?bits:previous[i];
                codes[x,y,z+2]=(byte)(below|(bits<<1));previous[i]=(byte)bits;
            }
        }
    }
}
