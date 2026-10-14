import React from 'react';
import { useStore } from './store';
export interface Props { title: string; }
export function View(props: Props) {
  const store = useStore();
  return <div onClick={() => store.save()}>{props.title}</div>;
}
