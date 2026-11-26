"""[Godhome] 生成 data/bosses.json 初稿（只在阶段 0 跑一次；之后 bosses.json 是手工维护的唯一数据源）。

输入（都在 reference/extracted/，不进 git）：
  lang.json   游戏本地化表 EN/ZH（tools/dump_lang.py + lang_table.py 生成）
  scan.jsonl  场景扫描结果（tools/scan_scenes.py 生成）
用法: python -I bootstrap_bosses.py <lang.json> <scan.jsonl> <out bosses.json> [--force]
输出文件已经存在时不写（现在的 data/bosses.json 有大量手写的 arena 配置，这个初稿生成器重建不出来，
Codex 审计 0.10.3）；真要覆盖加 --force。输出写到一个新文件再手工合并。

下面 SPEC 里每条只写"人工判断"的部分：用哪个标题键/图鉴条目取官方名字、在哪个场景、Boss 物体名、
需要的剧情标记、已知的特殊处理。场景出口、血量、地区名都从扫描数据里取，避免手抄出错。
"""
import json
import os
import sys

# act: 按常规流程推定的幕（阶段 1 实机核对）
# scene: Boss 所在场景；host: 若 Boss 在附加场景里，主场景名（传送进主场景，再由加载器加载附加场景）
# pd: 进场前要临时设置的 PlayerData（只在快照环境里改，离开模式时还原）
# battle: 场景里的 BattleScene 物体（可能是杂兵波次，阶段 1 决定跳不跳）
SPEC = [
    # ---------------- 第一幕 ----------------
    dict(id="moss_mother", title="MOSSBONE_MOTHER", act=1, scene="Tut_03", boss=["Mossbone Mother"],
         defeat=["defeatedMossMother"], encounter=["encounteredMossMother"], battle=["Battle Scene"],
         notes="第一个 Boss，血量只有 120。场景同时是去骸底镇的通道，有剧情门 door1 firstExit。"),
    dict(id="bell_beast", title="BELLBEAST", act=1, scene="Bone_05_boss", host="Bone_05", boss=["Bone Beast"],
         defeat=["defeatedBellBeast"], encounter=["encounteredBellBeast"],
         notes="附加场景 Bone_05_boss，由 Bone_05 的 Boss Additive Loader 在 defeatedBellBeast=false 时加载。打完连着丝之心记忆演出。"),
    dict(id="lace_1", title="LACE", journal="Lace", act=1, scene="Bone_East_12", boss=["Lace Boss1"],
         defeat=["defeatedLace1"], encounter=["encounteredLace1"],
         notes="蕾丝第一战。场景里有 DEMO 用的战斗门，注意别触发。"),
    dict(id="fourth_chorus", title="SONG_GOLEM", act=1, scene="Bone_East_08_boss_golem", host="Bone_East_08", boss=["SG_head"],
         defeat=["defeatedSongGolem"], encounter=["encounteredSongGolem"], pd={"hasBrolly": True},
         notes="附加场景。加载条件 defeatedSongGolem=false 且 hasBrolly=true（需要先拿到伞）。另有 _rest 附加场景是战前休眠状态。"),
    dict(id="savage_beastfly", title="GIANT_BONE_FLYER", act=1, scene="Ant_19", boss=["Bone Flyer Giant"],
         defeat=["defeatedBoneFlyerGiant"],
         notes="野兽教堂（猎者小径）的纹章试炼战，血量 550。场景出口 door_memoryEnd 说明它和纹章记忆演出连在一起。"),
    dict(id="sister_splinter", title="SPLINTER_QUEEN", act=1, scene="Shellwood_18", boss=["Splinter Queen"],
         defeat=["defeatedSplinterQueen"], encounter=["encounteredSplinterQueen"]),
    dict(id="skull_tyrant", title="SKULL_KING", act=1, scene="Bone_15", boss=["Skull King"],
         defeat=["skullKingDefeated"], notes="场景里有 Pierce Dmg Receiver（hp 9999999）辅助物体，统计血量时要排除。"),
    dict(id="widow", title="SPINNER", act=1, scene="Belltown_Shrine", boss=["Spinner Boss"],
         defeat=["spinnerDefeated"], encounter=["encounteredSpinner"],
         notes="有 Fake Death 状态（假死）；打完给针琴，并触发钟心镇剧情。出口有 door_wakeOnGround。"),
    dict(id="great_conchflies", title="DRILLERS", journal="Coral Conch Driller Giant", act=1, scene="Coral_11", boss=["Driller A"],
         defeat=["defeatedCoralDrillers"], encounter=["encounteredCoralDrillers"],
         notes="双 Boss；静态扫描只看到 Driller A，第二只可能运行时生成。Boss Scene 里有 needolin_capture_timer。"),
    dict(id="moorwing", title="VAMPIRE_GNAT", act=1, scene="Greymoor_05_boss", host="Greymoor_05", boss=["Vampire Gnat"],
         defeat=["defeatedVampireGnatBoss"], encounter=["encounteredVampireGnatBoss"],
         notes="附加场景。另有备用地点 Greymoor_08_boss（未去过钟心镇时出现，加载条件 visitedBellhart=false）。"),
    dict(id="phantom", title="PHANTOM", act=1, scene="Organ_01", boss=["Phantom"], defeat=["defeatedPhantom"],
         notes="废鸣管风琴。场景里有 thread_memory_lace 的深层记忆入口，别误触。"),
    dict(id="last_judge", title="LAST_JUDGE", act=1, scene="Coral_Judge_Arena", boss=["Last Judge"],
         defeat=["defeatedLastJudge"], encounter=["encounteredLastJudge"],
         notes="第一幕最终 Boss。场景里有大门、守门朝圣者 NPC、跳蚤旅团到达点（door_caravanTravelEnd），状态多。"),
    dict(id="forebrothers", title="DOCK_GUARD_DUO", journal="Dock Guard Thrower", act=1, scene="Dock_09",
         boss=["Dock Guard Slasher", "Dock Guard Thrower"], defeat=["defeatedDockForemen"], encounter=["encounteredDockForemen"],
         notes="双 Boss（西格尼斯 720 / 格隆 520）。Dock_02b 里还有一个不可击杀的格隆遭遇（hp 9999）。"),
    dict(id="gurr", title="HUNTER_TRAPPER", act=1, scene="Bone_East_18b", boss=["Bone Hunter Trapper"],
         defeat=["defeatedAntTrapper"], encounter=["encounteredAntTrapper"],
         notes="场景里有大量 Intro Barb 和陷阱尖刺 FSM，开场演出较长。"),
    dict(id="craggler", title=None, journal="Roof Crab", act=1, scene="Crawl_04", boss=["Roof Crab"],
         defeat=["roofCrabDefeated"], encounter=["roofCrabEncountered"], tier="mini",
         notes="小 Boss（hp 200，无标题卡）。"),
    # ---------------- 第二幕 ----------------
    dict(id="lugoli", title="CHEF", act=2, scene="Dust_Chef", boss=["Roachkeeper Chef (1)"], defeat=["defeatedRoachkeeperChef"],
         battle=["Battle Scene"], notes="战斗由 Battle Scene 驱动，可能先有蟑螂杂兵。"),
    dict(id="groal", title="SWAMP_SHAMAN", act=2, scene="Shadow_18", boss=["Swamp Shaman"], defeat=["DefeatedSwampShaman"],
         battle=["Battle Scene"], notes="有 Boss Summon Crew（召唤杂兵）。"),
    dict(id="crawfather", title="CROWFATHER", journal="Crawfather", act=2, scene="Room_CrowCourt_02", boss=["Crawfather"],
         defeat=["defeatedCrowCourt"], encounter=["encounteredCrowCourt"], battle=["Battle Scene"],
         notes="鸦虫审判庭。唯一出口 top1 通往 Room_CrowCourt，Battle Scene 有 Revisit 状态。"),
    dict(id="father_of_the_flame", title="WICKER_BUG", journal="Wisp Pyre Effigy", act=2, scene="Wisp_07", boss=[],
         defeat=["defeatedWispPyreEffigy"],
         notes="静态扫描在 Wisp_07 只找到 defeatedWispPyreEffigy 标记，没找到 Boss 的 HealthManager——Boss 可能是运行时生成的预制体。阶段 1 必须实机确认。"),
    dict(id="voltvyrm", title="ZAPNEST_BOSS", journal="Zap Core Enemy", act=2, scene="Coral_29", boss=["Zap Core Enemy"],
         defeat=["defeatedZapCoreEnemy"], notes="唯一出口 left1（来自 Coral_35b 的门）。"),
    dict(id="raging_conchfly", title="DRILLER_SOLO", journal="Coral Conch Driller Giant", act=2, scene="Coral_27",
         boss=["Coral Conch Driller Giant Solo"], defeat=["defeatedCoralDrillerSolo"], encounter=["encounteredCoralDrillerSolo"],
         battle=["Battle Scene"]),
    dict(id="broodmother", title="BROODMOTHER", journal="Slab Fly Broodmother", act=2, scene="Slab_16b", boss=["Slab Fly Broodmother"],
         defeat=["defeatedBroodMother"], battle=["Battle Scene Broodmother"]),
    dict(id="first_sinner", title="FIRST_WEAVER", act=2, scene="Slab_10b", boss=["First Weaver"], encounter=["encounteredFirstWeaver"],
         defeat=["defeatedFirstWeaver"], notes="有 CamLock Battle Intro 和 Outro 演出；出口 door_wakeOnGround。"),
    dict(id="cogwork_dancers", title="COGWORK_DANCERS", journal="Clockwork Dancer", act=2, scene="Cog_Dancers_boss", host="Cog_Dancers",
         boss=["Dancer A", "Dancer B"], defeat=["defeatedCogworkDancers"], encounter=["encounteredCogworkDancers"],
         notes="附加场景，加载器没有 PlayerData 条件（由 FSM 控制）。双 Boss。"),
    dict(id="trobbio", title="TROBBIO", act=2, scene="Library_13", boss=["Trobbio"], defeat=["defeatedTrobbio"], encounter=["encounteredTrobbio"],
         notes="和痛苦的特罗比奥共用 Library_13，靠剧情标记区分出场的是哪个。"),
    dict(id="the_unravelled", title="WARD_BOSS", journal="Conductor Boss", act=2, scene="Ward_02_boss", host="Ward_02",
         boss=["Conductor Boss"], defeat=["wardBossDefeated"], pd={"WillLoadWardBoss": True},
         notes="附加场景，加载条件 WillLoadWardBoss=true。有 Head Invuln Check（头部无敌判定）。"),
    dict(id="second_sentinel", title="SONG_KNIGHT", act=2, scene="Hang_17b", boss=["Song Knight"],
         defeat=["defeatedSongChevalierBoss"], encounter=["encounteredSongChevalierBoss"],
         notes="专用场景 Hang_17b（Boss Scene - To Additive Load）。Hang_07 里也有一个 Song Knight，可能是另一种出场。"),
    dict(id="lace_2", title="LACE", journal="Lace", act=2, scene="Song_Tower_01", boss=["Lace Boss2 New"],
         defeat=["defeatedLaceTower"], encounter=["encounteredLaceTower"],
         notes="蕾丝之塔。打完接丝之心记忆和过场（door_cutsceneEndLaceTower / door_cinematicEnd），必须拦截。"),
    dict(id="grand_mother_silk", title="SILK", journal="Silk Boss", act=2, scene="Cradle_03", boss=["Silk Boss"],
         encounter=["encounteredSilk"], defeat=[],
         notes="第二幕结局 Boss。打完进结局动画，必须拦截结局/存档流程。"),
    dict(id="garmond_zaza", title="GARMOND", journal="Garmond_Zaza", act=2, scene="Library_09", boss=["Garmond Fighter"],
         defeat=["garmondLibraryDefeatedHornet"], tier="spar",
         notes="切磋战（NPC），标记名的意思是“加蒙德打赢了大黄蜂”，胜负条件和普通 Boss 不同，阶段 1 单独处理。"),
    dict(id="cogwork_clapper", title=None, journal="Song Automaton Ball", act=2, scene="Cog_07",
         boss=["Song Automaton Ball R", "Song Automaton Ball L"], battle=["Battle Scene"], tier="mini",
         notes="小 Boss（无标题卡），三个球体（190/190/145）。"),
    dict(id="shakra", title="MAPPER", journal="Shakra", act=2, scene="Greymoor_08_mapper", host="Greymoor_08", boss=["Mapper Spar NPC"],
         tier="spar", notes="沙克拉切磋战，附加场景。具体触发条件待查。"),
    # ---------------- 第三幕 ----------------
    dict(id="clover_dancers", title="CLOVER_DANCERS", journal="Clover Dancer", act=3, scene="Clover_10", boss=["Dancer A", "Dancer B"],
         encounter=["encounteredCloverDancers"], defeat=["defeatedCloverDancers"],
         notes="失落的翠庭。场景里有绿王子 NPC 对话。双 Boss。"),
    dict(id="palestag", title="WHITE_CLOVERSTAG", journal="Cloverstag White", act=3, scene="Clover_19", boss=["Cloverstag White Boss"],
         defeat=["defeatedWhiteCloverstag"], encounter=["encounteredWhiteCloverstag"]),
    dict(id="nyleth", title="FLOWER_QUEEN", act=3, scene="Shellwood_11b_Memory", boss=["Flower Queen Boss"],
         defeat=["defeatedFlowerQueen"], encounter=["encounteredFlowerQueen"], memory=True,
         notes="记忆场景（入口 door_wakeInMemory），需要按记忆场景的方式进入和退出。"),
    dict(id="crust_king_khann", title="CORAL_KING", act=3, scene="Memory_Coral_Tower", boss=["Coral King"],
         defeat=["defeatedCoralKing"], encounter=["encounteredCoralKing"], memory=True,
         battle=["Battle Scene Chamber 1", "Battle Scene Chamber 2", "Battle Scene Chamber 3", "Battle Scene Chamber 4"],
         notes="记忆场景，Boss 前有 4 个房间的杂兵战（珊瑚尖塔），重打时要跳过或直接传到 Boss 房。场景 46MB，加载慢。"),
    dict(id="karmelita", title="HUNTER_QUEEN", act=3, scene="Memory_Ant_Queen", boss=["Hunter Queen Boss"], memory=True,
         defeat=["defeatedAntQueen"], battle=["Battle Scene"],
         notes="记忆场景。有两只 Bone Hunter Fly Chief（130）随从。"),
    dict(id="seth", title="SETH", act=3, scene="Shellwood_22", boss=["Seth"], defeat=["defeatedSeth"], encounter=["encounteredSeth"]),
    dict(id="lost_garmond", title="GARMOND_BT", journal="Garmond", act=3, scene="Coral_33", boss=["Garmond Black Threaded Fighter"],
         defeat=["garmondBlackThreadDefeated"]),
    dict(id="plasmified_zango", title="BLUE_ASSISTANT", act=3, scene="Crawl_10", boss=["Blue Assistant"], defeat=[],
         notes="击败标记待查（PlayerData 里没有直接对应的 defeated 字段）。"),
    dict(id="pinstress", title="PINSTRESS", journal="Pinstress Boss", act=3, scene="Peak_07", boss=["Pinstress Boss"], defeat=[],
         notes="击败标记待查。场景出口很多（8 个），入口选择要实机确认。"),
    dict(id="watcher_at_the_edge", title="GREY_CORAL_WARRIOR", journal="Coral Warrior Grey", act=3, scene="Coral_39",
         boss=["Coral Warrior Grey"], defeat=["defeatedGreyWarrior"]),
    dict(id="bell_eater", title="BIG_CENTIPEDE", journal="Giant Centipede", act=3, scene="Bellway_Centipede_Arena",
         boss=["Giant Centipede Head", "Giant Centipede Butt"], defeat=[],
         notes="出口是动态门 [dynamic]:door_centipedeExit，体节 Coil 是 hp 10000 的不可击杀部件。击败标记待查。"),
    dict(id="summoned_saviour", title="ABYSS_MASS", act=3, scene="Bone_Steel_Servant", boss=["Abyss Mass"],
         encounter=["EncounteredSummonedSaviour"], battle=["Battle Scene"], defeat=[]),
    dict(id="tormented_trobbio", title="TORMENTED_TROBBIO", act=3, scene="Library_13", boss=["Tormented Trobbio"],
         defeat=["defeatedTormentedTrobbio"], encounter=["encounteredTormentedTrobbio"],
         notes="和特罗比奥共用 Library_13。"),
    dict(id="skull_tyrant_invasion", title="SKULL_KING", act=3, scene="Bonetown_boss", host="Bonetown", boss=["Skull King"],
         defeat=["skullKingKilled"], encounter=["EncounteredBonetownBoss"], pd={"skullKingWillInvade": True},
         notes="颅骨暴君入侵骸底镇（附加场景）。加载条件 skullKingKilled=false 且 skullKingWillInvade=true 且 blackThreadWorld=false（另有一组条件）。"),
    dict(id="moss_mother_duo", title="MOSSBONE_MOTHER", act=3, scene="Weave_03", boss=["Mossbone Mother A", "Mossbone Mother B"],
         battle=["Boss Scene"], defeat=[], notes="织巢里的双苔藓之母（各 350）。击败标记待查。"),
    dict(id="savage_beastfly_2", title="GIANT_BONE_FLYER", act=2, scene="Bone_East_08_boss_beastfly", host="Bone_East_08",
         boss=["Bone Flyer Giant"], defeat=["defeatedBoneFlyerGiantGolemScene"],
         notes="远野的第二次兽蝇战（血量 650），由任务（许愿）控制加载：要求任务已接受、未完成。"),
    dict(id="huge_flea", title=None, journal="Giant Flea", act=3, scene="Arborium_08", boss=["Giant Flea"], tier="mini",
         notes="小 Boss（无标题卡）。"),
    dict(id="lost_lace", title="LOST_LACE", act=3, scene="Abyss_Cocoon", boss=["Lost Lace Boss"],
         encounter=["EncounteredLostLace"], defeat=[],
         notes="第三幕最终 Boss。场景出口只有自指的 door_entry / door_test，打完进结局。必须完整接管进出流程。"),
]


# 同名 Boss 的多次战斗，用来在界面上区分（我们自己起的，不是游戏文本）
VARIANTS = {
    "moss_mother": {"zh": "初遇", "en": "First Encounter"},
    "moss_mother_duo": {"zh": "织巢双生", "en": "Weavenest Pair"},
    "lace_1": {"zh": "初次交锋", "en": "First Duel"},
    "lace_2": {"zh": "高塔之战", "en": "Tower Duel"},
    "savage_beastfly": {"zh": "野兽教堂", "en": "Chapel of the Beast"},
    "savage_beastfly_2": {"zh": "远野再临", "en": "Far Fields Return"},
    "skull_tyrant": {"zh": "髓骨洞窟", "en": "The Marrow"},
    "skull_tyrant_invasion": {"zh": "入侵骸底镇", "en": "Bone Bottom Invasion"},
}


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    force = "--force" in sys.argv[1:]
    args = [a for a in sys.argv[1:] if a != "--force"]
    if len(args) != 3:
        sys.exit(__doc__)
    sys.argv[1:] = args
    if os.path.exists(sys.argv[3]) and not force:
        sys.exit(f"[Godhome] {sys.argv[3]} 已经存在，不覆盖（写到新文件再手工合并；真要覆盖加 --force）/ refusing to overwrite without --force")
    lang = json.load(open(sys.argv[1], encoding="utf-8"))
    scans = {}
    for line in open(sys.argv[2], encoding="utf-8"):
        sc = json.loads(line)
        if sc.get("scene"):
            scans[sc["scene"]] = sc
    titles, journal_sheet, zones = lang["Titles"], lang["Journal"], lang["Map Zones"]
    mapzone_names = ["NONE", "TEST_AREA", "PATH_OF_BONE", "GREYMOOR", "SHELLWOOD_THICKET", "RED_CORAL_GORGE", "CITY_OF_SONG",
                     "THE_SLAB", "GLOOM", "DUSTPENS", "BELLTOWN", "HUNTERS_NEST", "BONETOWN", "MOSS_CAVE", "PHARLOOM_BAY", "DOCKS",
                     "WILDS", "WEAVER_SHRINE", "BONECHURCH", "MOSSTOWN", "LIBRARY", "CLOVER", "UNDERSTORE", "COG_CORE", "PEAK",
                     "DUST_MAZE", "WARD", "HANG", "ARBORIUM", "CRADLE", "PILGRIMS_REST", "HALFWAY_HOUSE", "JUDGE_STEPS", "MEMORY",
                     "CRAWLSPACE", "WISP", "SWAMP", "ABYSS", "AQUEDUCT", "SURFACE", "FRONT_GATE", "CORAL_CAVERNS"]

    def title_name(stem):
        parts = [titles.get(f"{stem}_{s}", ["", ""]) for s in ("SUPER", "MAIN", "SUB")]
        en = " ".join(p[0].strip() for p in parts if p[0].strip())
        zh = ""
        for p in (p[1].strip() for p in parts if p[1].strip()):
            # 中文标题卡分行显示，拼起来时只在 & 两侧保留空格
            zh += (" " + p) if (p.startswith("&") or zh.endswith("&")) else p
        return en, zh

    out = []
    for s in SPEC:
        en = zh = ""
        if s.get("title"):
            en, zh = title_name(s["title"])
        if not en and s.get("journal"):
            key = "NAME_" + s["journal"].upper().replace(" ", "_")
            en, zh = journal_sheet.get(key, ["", ""])
        sc = scans.get(s["scene"], {})
        host = scans.get(s.get("host") or s["scene"], {})
        zone_idx = host.get("mapZone") if host.get("mapZone") is not None else sc.get("mapZone")
        zone_key = mapzone_names[zone_idx] if isinstance(zone_idx, int) and zone_idx < len(mapzone_names) else None
        # CORAL_CAVERNS 在 Map Zones 表里没有译名（游戏地图上归在卡拉卡沙川），用 RED_CORAL_GORGE 的名字
        zone_en, zone_zh = zones.get({"CORAL_CAVERNS": "RED_CORAL_GORGE"}.get(zone_key, zone_key), ["", ""]) if zone_key else ("", "")
        # 要加载的场景：附加场景 Boss 是宿主场景。入口一律是加载时建的 door_dreamEnter（arena.door，见 docs/arena.md），
        # 这里不生成 arena 段：用 tools/arena_draft.py 出草稿、实机核对后再写进表。
        entry_scene = s.get("host") or s["scene"]
        loader = None
        for a in host.get("additive", []):
            if a.get("scene") == s["scene"]:
                loader = {"object": a.get("go"), "tests": a.get("tests"), "quests": a.get("quests")}
        hp = {b: sc.get("health", {}).get(b) for b in s["boss"]}
        entry = {
            "id": s["id"],
            "name": {"zh": zh, "en": en},
            "variant": VARIANTS.get(s["id"]),
            "titleKey": s.get("title"),
            "tier": s.get("tier", "boss"),
            "act": s["act"],
            "region": {"key": zone_key, "zh": zone_zh, "en": zone_en},
            "scene": s["scene"],
            "additive": bool(s.get("host")),
            "hostScene": s.get("host"),
            "additiveLoader": loader,
            "entry": {"scene": entry_scene},
            "bossObjects": s["boss"],
            "hp": hp,
            "flags": {"defeated": s.get("defeat", []), "encountered": s.get("encounter", [])},
            "prerequisites": {"playerData": s.get("pd", {})},
            "notes": s.get("notes", ""),
        }
        out.append(entry)

    doc = {
        "$comment": "[Godhome] Boss 数据表。代码里不硬编码任何 Boss，新增 Boss（例如 DLC）只需补一条。字段说明见 docs/boss-inventory.md。",
        "schemaVersion": 1,
        "gameBuild": "Steam buildid 22479045 / Unity 6000.0.50f1",
        "bosses": out,
    }
    json.dump(doc, open(sys.argv[3], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"bosses: {len(out)}")
    for e in out:
        missing = [b for b, v in e["hp"].items() if v is None]
        print(f'{e["id"]:24} {e["name"]["zh"]:12} {e["scene"]:28} zone={e["region"]["zh"]:8} load={e["entry"]["scene"]} hp={e["hp"]} {"MISSING " + str(missing) if missing else ""}')


if __name__ == "__main__":
    main()
