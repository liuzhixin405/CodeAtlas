import { Foo } from './foo';
import * as util from 'util';
export namespace Shapes { export const PI = 3; }
export abstract class Base<T> { abstract run(): void; }
export interface IRepo extends IBase { find(id: number): Foo; count: number; }
export enum Color { Red, Green }
export type Id = string;
export class Svc extends Base<Foo> implements IRepo, IOther {
  private repo: IRepo;
  count = 0;
  constructor(r: IRepo) { super(); this.repo = r; }
  find(id: number): Foo { return this.repo.find(id); }
  get total(): number { return this.count; }
}
export function top(x: Foo): void { const s = new Svc(null!); s.find(1); helper(); util.format('x'); }
export const arrow = (a: number) => a + 1;
const fexpr = function named() {};
