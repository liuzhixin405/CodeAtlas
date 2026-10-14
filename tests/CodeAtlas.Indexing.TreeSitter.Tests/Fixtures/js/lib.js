import x from './x';
class A extends B {
  x = 1;
  m() { this.n(); foo(); }
}
function f() { new A(); }
const g = () => 1;
const h = function() {};
