import { useState } from "react";
import { useForm } from "react-hook-form";
import { Button, Card } from "./Button";
import Panel from "./Panel";
import { track } from "./analytics";

function save() { track("save"); }

export default function Form({ onClose }: { onClose: () => void }) {
  const [v, setV] = useState("");
  const form = useForm();
  const handleClick = () => save();
  const onSubmit = (data: unknown) => track(data);
  return (
    <Panel>
      <Button onClick={handleClick} onBlur={save} onFocus={track} onKeyDown={onClose} title={handleClick} />
      <input onChange={(e) => setV(e.target.value)} value={v} />
      <form onSubmit={form.handleSubmit(onSubmit)} />
      <Card />
    </Panel>
  );
}