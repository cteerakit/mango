import { describe, expect, it } from "vitest";
import { positionLabel, purchaseTime, relativeTime, splitTeams } from "./format";

describe("format", () => {
  it("formats relative match times like the WinUI app", () => {
    const now = Date.UTC(2027, 0, 15);
    const ago = (s: number) => new Date(now - s * 1000).toISOString();
    expect(relativeTime(ago(0), now)).toBe("Just now");
    expect(relativeTime(ago(60), now)).toBe("1 minute ago");
    expect(relativeTime(ago(7200), now)).toBe("2 hours ago");
    expect(relativeTime(ago(86400), now)).toBe("1 day ago");
    expect(relativeTime(ago(2592000), now)).toBe("1 month ago");
    expect(relativeTime(ago(31536000), now)).toBe("1 year ago");
    expect(relativeTime(null, now)).toBe("");
  });

  it("formats purchase times as match clock", () => {
    expect(purchaseTime(null)).toBe("—");
    expect(purchaseTime(-65)).toBe("−01:05");
    expect(purchaseTime(0)).toBe("00:00");
    expect(purchaseTime(3601)).toBe("60:01");
  });

  it("labels positions", () => {
    expect(positionLabel(1)).toBe("Pos 1 · Carry (estimated)");
    expect(positionLabel(5, false)).toBe("Pos 5 · Hard support");
    expect(positionLabel(null)).toBe("Position unavailable");
  });

  it("splits teams with unknown sides in the lobby", () => {
    const teams = splitTeams([{ teamName: "radiant" }, { teamName: "Dire" }, { teamName: "" }]);
    expect([teams.radiant.length, teams.dire.length, teams.lobby.length]).toEqual([1, 1, 1]);
  });
});
