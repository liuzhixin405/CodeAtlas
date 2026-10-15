import React from "react";
import { BaseView } from "./BaseView";

export class Counter extends BaseView<{}> {
  handleClick = () => {
    this.inc();
    this.log();
  };
  inc() { this.refresh(); }
  render() { return <button onClick={this.handleClick} onBlur={this.missing} />; }
}

export class Plain extends React.Component {
  go() { this.stop(); }
  stop() {}
}