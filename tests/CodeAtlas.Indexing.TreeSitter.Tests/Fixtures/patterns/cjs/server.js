const path = require('path');
const grpc = require('@grpc/grpc-js');
const { v4: uuidv4 } = require('uuid');
const charge = require('./charge');
const { format, round: rnd } = require('./util');
const child = require('./logger').child;
const store = require('./store');

function _getData(callback) {
  const data = require('./data/rates.json');
  callback(data);
}

function convert(call, callback) {
  _getData((data) => {
    const v = rnd(data.x);
    callback(null, format(v));
  });
}

function pay(req) {
  child('paying');
  store.save(req);
  uuidv4();
  return charge(req);
}

function main() {
  grpc.loadPackageDefinition(path.join('a', 'b'));
  require('./tracing').start();
}

main();