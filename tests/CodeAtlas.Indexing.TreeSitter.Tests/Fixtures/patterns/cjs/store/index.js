class Store {
  save(x) { return this.#write(x); }
  #write(x) { return x; }
  async *rows() { yield 1; }
  get size() { return 0; }
  set size(v) {}
}

async function* scan() { yield 1; }

module.exports = new Store();
module.exports.scan = scan;