import { Controller, Get } from "@nestjs/common";
import { UsersService as Svc } from "./users.service";
import { HttpService } from "@nestjs/axios";

@Controller("users")
export class UsersController {
  private readonly cache: Svc;
  constructor(private readonly users: Svc, private readonly http: HttpService, plain: Svc) {}

  @Get()
  findAll() {
    this.http.get("/x");
    this.cache.list();
    return this.users.list();
  }
}