import { useCallback, useEffect, useState } from "react";
import { Pencil, Trash2 } from "lucide-react";
import { Button, Card, Dialog, Notice, Page, cx, inputClass } from "../components/ui";
import { api, errorText } from "../lib/api";
import { dash, shortDateTime } from "../lib/format";
import { useNotesVersion } from "../lib/hooks";
import type { NoteRow } from "../lib/types";

export function NotesPage() {
  const version = useNotesVersion();
  const [notes, setNotes] = useState<NoteRow[]>([]);
  const [editing, setEditing] = useState<NoteRow | null>(null);
  const [deleting, setDeleting] = useState<NoteRow | null>(null);
  const [text, setText] = useState("");
  const [error, setError] = useState("");

  const reload = useCallback(() => api.notesList().then(setNotes).catch((e) => setError(errorText(e))), []);
  useEffect(() => {
    reload();
  }, [reload, version]);

  const subtitle = notes.length === 0 ? "Saved player notes." : notes.length === 1 ? "1 saved note." : `${notes.length} saved notes.`;

  const save = async () => {
    if (!editing) return;
    try {
      await api.noteSave(editing.playerName, editing.playerId, text.trim());
      setEditing(null);
    } catch (e) {
      setError(errorText(e));
    }
  };
  const remove = async () => {
    if (!deleting) return;
    try {
      await api.noteDelete(deleting.key);
      setDeleting(null);
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <Page title="Notes" subtitle={subtitle} wide>
      {error && (
        <Notice tone="critical" onClose={() => setError("")}>
          {error}
        </Notice>
      )}
      <Card className="overflow-hidden">
        {notes.length === 0 ? (
          <p className="p-5 text-sm text-muted">No notes yet. Add notes from Match, or import a backup.</p>
        ) : (
          <table className="w-full text-sm">
            <thead className="border-b border-line bg-surface-2 text-left text-xs tracking-wide text-muted uppercase">
              <tr>
                {["Player name", "ID", "Content", "Likes", "Dislikes", "Date/time", ""].map((h, i) => (
                  <th key={i} className="px-4 py-2 font-semibold">
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {notes.map((note) => (
                <tr key={note.key} className="border-b border-line align-top last:border-b-0">
                  <td className="px-4 py-2.5 font-medium">
                    {dash(note.playerName)}
                    {note.isToxic && (
                      <span className="ml-2 rounded bg-caution-bg px-1.5 py-0.5 text-[11px] font-semibold text-caution">Toxic</span>
                    )}
                  </td>
                  <td className="tabular px-4 py-2.5 text-muted select-text">{dash(note.playerId)}</td>
                  <td className="max-w-md px-4 py-2.5 whitespace-pre-wrap">{dash(note.content)}</td>
                  <td className="tabular px-4 py-2.5">{note.likeCount}</td>
                  <td className="tabular px-4 py-2.5">{note.dislikeCount}</td>
                  <td className="px-4 py-2.5 whitespace-nowrap text-muted">{shortDateTime(note.updatedAt)}</td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    <div className="flex justify-end gap-1">
                      <Button
                        variant="ghost"
                        aria-label={`Edit note for ${note.playerName || note.playerId}`}
                        onClick={() => {
                          setText(note.content);
                          setEditing(note);
                        }}
                      >
                        <Pencil className="size-4" /> Edit
                      </Button>
                      <Button variant="ghost" aria-label={`Delete note for ${note.playerName || note.playerId}`} onClick={() => setDeleting(note)}>
                        <Trash2 className="size-4" /> Delete
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      <Dialog
        open={!!editing}
        title={editing?.playerName ? `Edit note — ${editing.playerName}` : "Edit note"}
        onClose={() => setEditing(null)}
        footer={
          <>
            <Button onClick={() => setEditing(null)}>Cancel</Button>
            <Button variant="primary" onClick={save}>
              Save
            </Button>
          </>
        }
      >
        <textarea aria-label="Note" value={text} onChange={(e) => setText(e.target.value)} className={cx(inputClass, "h-36 resize-none py-2")} />
      </Dialog>

      <Dialog
        open={!!deleting}
        title="Delete note"
        onClose={() => setDeleting(null)}
        footer={
          <>
            <Button onClick={() => setDeleting(null)}>Cancel</Button>
            <Button variant="danger" onClick={remove}>
              Delete
            </Button>
          </>
        }
      >
        Delete the note for {deleting?.playerName || deleting?.playerId || "this player"}? This cannot be undone.
      </Dialog>
    </Page>
  );
}
