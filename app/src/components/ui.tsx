import { type ButtonHTMLAttributes, type ReactNode, useEffect, useId, useRef } from "react";
import { X } from "lucide-react";

export function cx(...classes: (string | false | null | undefined)[]) {
  return classes.filter(Boolean).join(" ");
}

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";

export function Button({
  variant = "secondary",
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return (
    <button
      type="button"
      className={cx(
        "inline-flex h-9 items-center justify-center gap-2 rounded-md px-3.5 text-sm font-medium transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-50",
        variant === "primary" && "bg-accent text-accent-fg hover:bg-accent-strong",
        variant === "secondary" && "border border-line bg-surface-2 text-fg hover:bg-surface-3",
        variant === "ghost" && "text-muted hover:bg-surface-2 hover:text-fg",
        variant === "danger" && "bg-critical text-white hover:opacity-90",
        className,
      )}
      {...props}
    />
  );
}

type Tone = "success" | "critical" | "caution" | "neutral";

const toneOn: Record<Tone, string> = {
  success: "border-success bg-success-bg text-success",
  critical: "border-critical bg-critical-bg text-critical",
  caution: "border-caution bg-caution-bg text-caution",
  neutral: "border-accent bg-surface-3 text-fg",
};

/** Square icon button that can show an on/off state. */
export function IconToggle({
  pressed,
  tone = "neutral",
  label,
  children,
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { pressed?: boolean; tone?: Tone; label: string }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={pressed}
      className={cx(
        "inline-flex size-8 items-center justify-center rounded-md border transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-40",
        pressed ? toneOn[tone] : "border-line bg-surface-2 text-muted hover:bg-surface-3 hover:text-fg",
        className,
      )}
      {...props}
    >
      {children}
    </button>
  );
}

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return <section className={cx("rounded-xl border border-line bg-surface", className)}>{children}</section>;
}

export function CardSection({ title, description, children }: { title: string; description?: ReactNode; children?: ReactNode }) {
  return (
    <Card className="p-5">
      <h2 className="font-display text-base font-semibold">{title}</h2>
      {description && <p className="mt-1 text-sm text-muted">{description}</p>}
      {children && <div className="mt-4 space-y-4">{children}</div>}
    </Card>
  );
}

export function Page({ title, subtitle, actions, children, wide }: {
  title: string;
  subtitle?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  wide?: boolean;
}) {
  return (
    <div className="h-full overflow-y-auto">
      <div className={cx("mx-auto space-y-5 px-8 py-7", wide ? "max-w-none" : "max-w-3xl")}>
        <header className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="font-display text-2xl font-semibold tracking-tight">{title}</h1>
            {subtitle && <p className="mt-1 text-sm text-muted">{subtitle}</p>}
          </div>
          {actions && <div className="flex gap-2">{actions}</div>}
        </header>
        {children}
      </div>
    </div>
  );
}

export function Switch({ label, description, checked, onChange, disabled }: {
  label: string;
  description?: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}) {
  const id = useId();
  return (
    <div className="flex items-start justify-between gap-6">
      <label htmlFor={id} className="min-w-0 cursor-pointer">
        <span className="block text-sm font-medium">{label}</span>
        {description && <span className="mt-0.5 block text-sm text-muted">{description}</span>}
      </label>
      <button
        id={id}
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={cx(
          "relative mt-0.5 h-6 w-11 shrink-0 rounded-full border transition-colors disabled:opacity-50",
          checked ? "border-accent bg-accent" : "border-line bg-surface-3",
        )}
      >
        <span
          className={cx(
            "absolute top-0.5 size-4.5 rounded-full shadow transition-transform",
            checked ? "translate-x-5.5 bg-accent-fg" : "translate-x-0.5 bg-muted",
          )}
        />
      </button>
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: (id: string) => ReactNode }) {
  const id = useId();
  return (
    <div>
      <label htmlFor={id} className="mb-1.5 block text-sm font-medium">
        {label}
      </label>
      {children(id)}
      {hint && <p className="mt-1.5 text-sm text-muted">{hint}</p>}
    </div>
  );
}

export const inputClass =
  "h-9 w-full rounded-md border border-line bg-surface-2 px-3 text-sm text-fg placeholder:text-faint focus:border-accent focus:outline-none";

export function Notice({ tone = "neutral", children, onClose }: { tone?: Tone; children: ReactNode; onClose?: () => void }) {
  return (
    <div
      role="status"
      className={cx(
        "flex items-start justify-between gap-3 rounded-lg border px-4 py-3 text-sm",
        tone === "neutral" ? "border-line bg-surface-2 text-fg" : toneOn[tone],
      )}
    >
      <div>{children}</div>
      {onClose && (
        <button type="button" aria-label="Dismiss" onClick={onClose} className="opacity-70 hover:opacity-100">
          <X className="size-4" />
        </button>
      )}
    </div>
  );
}

export function Spinner({ className }: { className?: string }) {
  return (
    <span
      role="progressbar"
      aria-label="Loading"
      className={cx("inline-block size-4 animate-spin rounded-full border-2 border-line border-t-accent", className)}
    />
  );
}

export function Dialog({ title, open, onClose, children, footer }: {
  title: string;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  footer: ReactNode;
}) {
  const panel = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    panel.current?.querySelector<HTMLElement>("textarea, input, select, button")?.focus();
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/55 p-6" onMouseDown={onClose}>
      <div
        ref={panel}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="w-full max-w-md rounded-xl border border-line bg-surface shadow-2xl"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <h2 className="px-5 pt-5 font-display text-lg font-semibold">{title}</h2>
        <div className="px-5 py-4 text-sm">{children}</div>
        <div className="flex justify-end gap-2 rounded-b-xl border-t border-line bg-surface-2 px-5 py-3">{footer}</div>
      </div>
    </div>
  );
}
