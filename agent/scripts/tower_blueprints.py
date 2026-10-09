"""Generate the starter tower blueprints (PlanBuild .blueprint) into mod/blueprints:

    uv run python scripts/tower_blueprints.py

- watchtower: a wooden watchtower, 4 x 4 m, four storeys and a lookout with a covered beacon fire at 8 m.
- lighthouse: a stone lighthouse, 6 x 6 m, the same height, with a parapet and a beacon under a timber canopy on stone pillars.

Piece sizes and pivots are the game's (snap points from `scenario.py pieces`): floors pivot on their top surface,
walls, poles and stone blocks on their centre; a step ladder climbs 2 m towards -z from its pivot (bottom end at z=+1).
"""

import math
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "mod" / "blueprints"
STOREY = 2.0
STOREYS = 4                 # walls up to 8 m
TOP = STOREY * STOREYS      # the lookout floor
TURN = (0.0, math.sqrt(0.5), 0.0, math.sqrt(0.5))   # 90 degrees about y
NONE = (0.0, 0.0, 0.0, 1.0)


class Blueprint:
    def __init__(self, name: str, title: str, description: str) -> None:
        self.name, self.title, self.description = name, title, description
        self.pieces: list[tuple[str, float, float, float, tuple]] = []

    def add(self, prefab: str, x: float, y: float, z: float, rot: tuple = NONE) -> None:
        self.pieces.append((prefab, x, y, z, rot))

    def write(self) -> Path:
        lines = [f"#Name:{self.title}", "#Creator:Sapienheim", f"#Description:{self.description}", "#Category:Sapienheim",
                 "#Pieces"]
        for prefab, x, y, z, q in self.pieces:
            lines.append(";".join([prefab, "Building", *(f"{v:g}" for v in (x, y, z, *q)), "", "1", "1", "1"]))
        path = OUT / f"{self.name}.blueprint"
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path


def ladder_x(storey: int) -> float:
    """Ladders alternate sides, so each one starts on solid floor beside the hole the one below comes up through."""
    return -1.5 if storey % 2 == 0 else 1.5


def interior_floor(bp: Blueprint, y: float, hole_x: float | None) -> None:
    """A 4 x 4 m floor of 1 x 1 m boards, leaving a 1 x 2 m hole where the ladder below comes up."""
    for x in (-1.5, -0.5, 0.5, 1.5):
        for z in (-1.5, -0.5, 0.5, 1.5):
            if hole_x is not None and x == hole_x and z in (-0.5, 0.5):
                continue
            bp.add("wood_floor_1x1", x, y, z)


def ladders_and_floors(bp: Blueprint) -> None:
    for storey in range(STOREYS):
        y = storey * STOREY
        bp.add("wood_stepladder", ladder_x(storey), y, 0.0)
        interior_floor(bp, y + STOREY, ladder_x(storey))


def beacon(bp: Blueprint) -> None:
    bp.add("fire_pit", 0.0, TOP, 0.0)


def watchtower() -> Blueprint:
    bp = Blueprint("watchtower", "Wooden Watchtower",
                   "A wooden watchtower: four storeys, ladders inside, and a lookout at 8 m with a covered beacon fire.")
    for x in (-1, 1):
        for z in (-1, 1):
            bp.add("wood_floor", x, 0.0, z)
    for storey in range(STOREYS):
        y = storey * STOREY
        # Corner posts carry everything up to the lookout.
        for x in (-2, 2):
            for z in (-2, 2):
                bp.add("wood_pole2", x, y + 1, z)
        wall = "wood_wall_half" if storey == STOREYS - 1 else "woodwall"   # top storey: a band of windows
        wy = y + (0.5 if wall == "wood_wall_half" else 1)
        for a in (-1, 1):
            if storey == 0 and a == 1:
                bp.add("wood_door", 1, 1, -2)
            else:
                bp.add(wall, a, wy, -2)
            bp.add(wall, a, wy, 2)
            bp.add(wall, -2, wy, a, TURN)
            bp.add(wall, 2, wy, a, TURN)
    # Beams round the top of the walls, under the lookout's edge.
    for a in (-1, 1):
        bp.add("wood_beam", a, TOP, -2)
        bp.add("wood_beam", a, TOP, 2)
        bp.add("wood_beam", -2, TOP, a, TURN)
        bp.add("wood_beam", 2, TOP, a, TURN)
    ladders_and_floors(bp)
    # Lookout: a waist-high wall, posts up to a canopy that keeps the rain off the beacon.
    for a in (-1, 1):
        bp.add("wood_wall_half", a, TOP + 0.5, -2)
        bp.add("wood_wall_half", a, TOP + 0.5, 2)
        bp.add("wood_wall_half", -2, TOP + 0.5, a, TURN)
        bp.add("wood_wall_half", 2, TOP + 0.5, a, TURN)
    for x in (-2, 2):
        for z in (-2, 2):
            bp.add("wood_pole2", x, TOP + 1, z)
    for a in (-1, 1):
        bp.add("wood_beam", a, TOP + 2, -2)
        bp.add("wood_beam", a, TOP + 2, 2)
        bp.add("wood_beam", -2, TOP + 2, a, TURN)
        bp.add("wood_beam", 2, TOP + 2, a, TURN)
    for x in (-1, 1):
        for z in (-1, 1):
            bp.add("wood_floor", x, TOP + 2, z)
    beacon(bp)
    return bp


def lighthouse() -> Blueprint:
    bp = Blueprint("lighthouse", "Stone Lighthouse",
                   "A stone lighthouse: 1 m thick walls, ladders inside, a parapet at 8 m and a beacon under a timber canopy on stone pillars. "
                   "Needs a stonecutter.")
    for x in (-1, 1):
        for z in (-1, 1):
            bp.add("stone_floor_2x2", x, -0.5, z)
    for storey in range(STOREYS):
        y = storey * STOREY
        # Front and back: a 4 x 2 block and a stack of two 2 x 1s, offset so the joints don't line up.
        bp.add("stone_wall_4x2", -1, y + 1, -2.5)
        if storey == 0:
            bp.add("wood_door", 2, 1, -2.5)
        else:
            bp.add("stone_wall_2x1", 2, y + 0.5, -2.5)
            bp.add("stone_wall_2x1", 2, y + 1.5, -2.5)
        bp.add("stone_wall_4x2", 1, y + 1, 2.5)
        bp.add("stone_wall_2x1", -2, y + 0.5, 2.5)
        bp.add("stone_wall_2x1", -2, y + 1.5, 2.5)
        for x in (-2.5, 2.5):
            if storey == 2:
                # A window on each side: the upper course leaves a 1 x 1 m gap.
                bp.add("stone_wall_2x1", x, y + 0.5, -1, TURN)
                bp.add("stone_wall_2x1", x, y + 0.5, 1, TURN)
                bp.add("stone_wall_2x1", x, y + 1.5, -1, TURN)
                bp.add("stone_wall_1x1", x, y + 1.5, 1.5)
            else:
                bp.add("stone_wall_4x2", x, y + 1, 0, TURN)
    ladders_and_floors(bp)
    # Parapet on the wall tops, then pillars at the corners carrying a canopy over the beacon.
    for x in (-2, 0, 2):
        bp.add("stone_wall_2x1", x, TOP + 0.5, -2.5)
        bp.add("stone_wall_2x1", x, TOP + 0.5, 2.5)
    for x in (-2.5, 2.5):
        for z in (-1, 1):
            bp.add("stone_wall_2x1", x, TOP + 0.5, z, TURN)
    for x in (-2.5, 2.5):
        for z in (-2.5, 2.5):
            bp.add("stone_pillar", x, TOP + 2, z)
    # The canopy is timber: stone slabs barely span sideways (only the ones on a pillar stood), wood on stone does.
    for a in (-2, 0, 2):
        bp.add("wood_beam", a, TOP + 3, -2.5)
        bp.add("wood_beam", a, TOP + 3, 2.5)
        bp.add("wood_beam", -2.5, TOP + 3, a, TURN)
        bp.add("wood_beam", 2.5, TOP + 3, a, TURN)
    for x in (-2, 0, 2):
        for z in (-2, 0, 2):
            bp.add("wood_floor", x, TOP + 3, z)
    beacon(bp)
    return bp


if __name__ == "__main__":
    for bp in (watchtower(), lighthouse()):
        print(bp.write(), len(bp.pieces), "pieces")
