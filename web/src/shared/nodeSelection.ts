import type { NodeSummary } from './api/types';

/** 可授权的节点：授权以机器码为席位锚点，离线或运行中的节点也能签发；停用节点不选。 */
export function isNodeLicenseSelectable(node: NodeSummary): boolean {
  return node.isEnabled;
}
