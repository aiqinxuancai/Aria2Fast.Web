import {api} from './core.js';

// Choosing how to play a video in this browser: directly, remuxed (picture untouched) or transcoded, both over HLS.
const probe=document.createElement('video');
const MSE=window.ManagedMediaSource||window.MediaSource;
const WEBM={vp8:'vp8',vp9:'vp9',av1:'av01.0.08M.08',opus:'opus',vorbis:'vorbis'};

/** Whether this browser decodes a codec (RFC 6381 name from the server) in MP4, as used by HLS segments. */
export function decodes(codec,kind='video'){
  if(!codec)return false;
  const type=kind+'/mp4; codecs="'+codec+'"';
  try{if(MSE?.isTypeSupported?.(type))return true;}catch{}
  return probe.canPlayType(type)==='probably';
}
// Some browsers only answer for one of the two HEVC sample entry names.
export const videoSupported=v=>!!v?.codecString&&(v.codecString.startsWith('hvc1')?[v.codecString,v.codecString.replace('hvc1','hev1')]:[v.codecString]).some(c=>decodes(c));
export const audioSupported=a=>!a||decodes(a.codecString,'audio');
export const nativeHls=()=>!!probe.canPlayType('application/vnd.apple.mpegurl');
export const hlsPossible=()=>!!MSE||nativeHls();
const chromium=()=>!!navigator.userAgentData?.brands?.some(b=>/Chromium|Google Chrome|Microsoft Edge|Opera/.test(b.brand));
export const defaultAudio=info=>info.audio.find(a=>a.default)||info.audio[0]||null;

/** The file itself: MP4 / WebM everywhere they decode; MKV only in Chromium browsers, which read it although they do not advertise it. */
export function directPlayable(info,audio){
  const v=info.video;
  if(!v)return false;
  const webm=[WEBM[v.codec],audio&&WEBM[audio.codec]].filter(Boolean);
  if((info.container==='webm'||(info.container==='mkv'&&chromium()))&&webm.length===(audio?2:1)&&probe.canPlayType('video/webm; codecs="'+webm.join(', ')+'"'))return true;
  if(['mp4','m4v','mov'].includes(info.container)||(info.container==='mkv'&&chromium()))return videoSupported(v)&&audioSupported(audio);
  return false;
}

/** Direct when possible; remux when only the container or the audio is the problem; otherwise transcode. A non-default audio track needs HLS. */
export function choose(info,audioIndex){
  const audio=info.audio.find(a=>a.index===audioIndex)||defaultAudio(info);
  if(audio===defaultAudio(info)&&directPlayable(info,audio))return {mode:'direct'};
  if(!hlsPossible())return {mode:'direct'};
  if(videoSupported(info.video))return {mode:'copy',audioCopy:audioSupported(audio)};
  return {mode:'transcode'};
}

export const startSession=(episode,options)=>api('/library/episodes/'+encodeURIComponent(episode)+'/hls','POST',options);
export function endSession(id){
  if(id)fetch('/api/library/hls/'+encodeURIComponent(id),{method:'DELETE',keepalive:true,headers:{'X-Aria2Fast':'1'}}).catch(()=>{});
}

let loader;
/** hls.js (vendored, loaded only when needed) where Media Source Extensions exist; Safari's native HLS otherwise. */
export async function attachHls(video,url,start,onFatal){
  if(!MSE&&nativeHls())return attachNative(video,url,start);
  const Hls=(await (loader??=import('/vendor/hls.light.esm.js'))).default;
  if(!Hls.isSupported()){if(nativeHls())return attachNative(video,url,start);throw new Error('这个浏览器不支持 HLS 播放，请使用外部播放器');}
  // Segments are produced on demand, so the first byte of a segment can take a while on slow machines.
  const policy={maxTimeToFirstByteMs:95000,maxLoadTimeMs:180000,timeoutRetry:{maxNumRetry:1,retryDelayMs:0,maxRetryDelayMs:0},errorRetry:{maxNumRetry:2,retryDelayMs:1000,maxRetryDelayMs:6000}};
  const hls=new Hls({enableWorker:false,startPosition:start>0?start:-1,maxBufferLength:30,maxMaxBufferLength:120,backBufferLength:90,fragLoadPolicy:{default:policy}});
  hls.on(Hls.Events.ERROR,(event,data)=>{
    if(!data.fatal)return;
    let message=data.details||'播放出错';
    try{message=JSON.parse(data.response?.text||data.networkDetails?.responseText||'').error||message;}catch{}
    onFatal(message);
  });
  hls.loadSource(url);
  hls.attachMedia(video);
  return {destroy(){hls.destroy();}};
}
function attachNative(video,url,start){
  video.src=url;
  if(start>0)video.addEventListener('loadedmetadata',()=>{video.currentTime=start;},{once:true});
  return {destroy(){}};
}

const CODECS={h264:'H.264',hevc:'HEVC',av1:'AV1',vp9:'VP9',vp8:'VP8',mpeg2video:'MPEG-2',mpeg4:'MPEG-4',vc1:'VC-1',aac:'AAC',flac:'FLAC',opus:'Opus',mp3:'MP3',ac3:'AC-3',eac3:'E-AC-3',dts:'DTS',truehd:'TrueHD',vorbis:'Vorbis',alac:'ALAC'};
export const codecName=c=>CODECS[c]||String(c||'').toUpperCase();
export const channels=n=>n===1?'单声道':n===2?'立体声':n===6?'5.1':n===8?'7.1':n+' 声道';
export function describe(info){
  const v=info.video,a=defaultAudio(info);
  return [info.container.toUpperCase(),v?codecName(v.codec)+(v.bitDepth>8?' '+v.bitDepth+'bit':'')+' '+v.width+'×'+v.height:'',a?codecName(a.codec)+' '+channels(a.channels):''].filter(Boolean).join(' · ');
}
