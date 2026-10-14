import { helper } from './util/format';
export class Svc {
  find(id: number): string { return helper(id); }
}
