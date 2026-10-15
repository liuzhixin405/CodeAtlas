import { IClock } from "./users.service";

export class SystemClock implements IClock { now() { return Date.now(); } }
export class FakeClock implements IClock { now() { return 0; } }