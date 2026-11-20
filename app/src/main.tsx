import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import "./styles.css";

const scrollbarHideTimers = new WeakMap<Element, number>();

document.addEventListener("scroll", (event) => {
  const scroller = event.target === document ? document.documentElement : event.target;
  if (!(scroller instanceof Element)) return;

  window.clearTimeout(scrollbarHideTimers.get(scroller));
  scroller.setAttribute("data-scrolling", "");
  scrollbarHideTimers.set(scroller, window.setTimeout(() => {
    scroller.removeAttribute("data-scrolling");
    scrollbarHideTimers.delete(scroller);
  }, 800));
}, { capture: true, passive: true });

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
