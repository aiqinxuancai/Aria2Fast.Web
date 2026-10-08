// aria2 errorCode uses the exit-status definitions in its official manual.
const reasons = {
  1:'未知错误，请查看下载节点的 aria2 日志',
  2:'连接或下载超时', 3:'资源未找到', 4:'资源未找到次数达到上限',
  5:'下载速度低于节点设置的最低速度', 6:'网络连接错误',
  7:'aria2 退出时仍有未完成的下载', 8:'服务器不支持所需的断点续传',
  9:'磁盘空间不足', 10:'分块大小与 .aria2 控制文件不一致',
  11:'正在下载相同文件', 12:'正在下载相同哈希的种子', 13:'目标文件已存在',
  14:'文件重命名失败', 15:'无法打开已有文件', 16:'无法创建或截断文件，请检查目录权限',
  17:'文件读写失败', 18:'无法创建目录，请检查路径及权限', 19:'域名解析失败',
  20:'无法解析 Metalink 文件', 21:'FTP 命令失败', 22:'HTTP 响应头不正确或不符合预期',
  23:'重定向次数过多', 24:'HTTP 身份验证失败', 25:'无法解析种子编码，响应可能不是种子文件',
  26:'种子损坏或缺少必要信息', 27:'磁力链接无效', 28:'下载参数无效',
  29:'远端服务器暂时无法处理请求', 30:'无法解析 JSON-RPC 请求', 32:'校验和验证失败'
};

export function downloadError(task){
  if(task.status!=='error')return '';
  const code=String(task.errorCode??'');
  const reason=Object.hasOwn(reasons,code)?reasons[code]:'原因未明确，请查看下载节点的 aria2 日志';
  return (code&&code!=='0'?'错误码 '+code+'：':'')+reason+(task.errorMessage?'；原始信息：'+task.errorMessage:'');
}
