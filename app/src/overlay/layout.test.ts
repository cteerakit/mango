import { describe, expect, it } from "vitest";
import { clamp, defaultPosition } from "./layout";

describe("overlay layout", () => {
  it("anchors defaults to the screen edges", () => {
    expect(defaultPosition("player", 1920)).toEqual([0, 104]);
    expect(defaultPosition("power", 1920)).toEqual([1828, 86]);
    expect(defaultPosition("build", 2560)).toEqual([2166, 410]);
  });

  it("clamps widgets on screen", () => {
    expect(clamp(-5, -5, 100, 20, 1920, 1080)).toEqual([0, 0]);
    expect(clamp(1900, 1075, 100, 20, 1920, 1080)).toEqual([1820, 1060]);
  });
});
