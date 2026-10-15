import { createContext, useContext } from "react";

const Ctx = createContext<{ has: (x: number) => boolean } | null>(null);

export function usePermissions() {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error("no permission context");
  return ctx;
}
