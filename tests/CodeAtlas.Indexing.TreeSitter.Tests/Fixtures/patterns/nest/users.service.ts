import { Injectable } from "@nestjs/common";

export interface IUsersRepo { findAll(): string[]; }
export interface IClock { now(): number; }
export abstract class Notifier { abstract send(msg: string): void; }

@Injectable()
export class UsersService {
  private clock: IClock;
  constructor(private readonly repo: IUsersRepo, private notifier: Notifier) {}
  list() {
    this.notifier.send("x");
    this.clock.now();
    return this.repo.findAll();
  }
}