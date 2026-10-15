import { memo, forwardRef } from "react";
import React from "react";

export const Button = memo(forwardRef<HTMLButtonElement>((props, ref) => <button ref={ref} />));
export const Icon = React.memo(function Icon() { return <i />; });
function CardInner() { return <div />; }
export const Card = memo(CardInner);