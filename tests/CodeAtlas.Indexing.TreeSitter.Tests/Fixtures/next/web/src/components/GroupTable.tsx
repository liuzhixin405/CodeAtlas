"use client";
import { useGroups } from "@/hooks/useGroups";
import { usePermissions } from "../contexts/Permission";
import * as Fmt from "./fmt";
import * as Ui from "./ui";

function Row() { return <span />; }

export default function GroupTable() {
  const { has } = usePermissions();
  const data = useGroups(1);
  const list = data.filter((x: number) => has(x));
  Fmt.label(list);
  return <Ui.Panel><Row /></Ui.Panel>;
}
