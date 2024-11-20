// Copyright 2024 Robert Adams (misterblue@misterblue.com)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Reflection;
using System.Threading.Tasks;

using OpenSim.Framework;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using log4net;
using OpenSim.Region.Framework.Scenes;

namespace WebRtcVoice
{
    public class WebRtcJanusService : ServiceBase, IWebRtcVoiceService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[JANUS SERVICE]";

        private readonly IConfigSource _Config;
        private bool _Enabled = false;

        private string _JanusServerURI = String.Empty;
        private string _JanusAPIToken = String.Empty;
        private string _JanusAdminURI = String.Empty;
        private string _JanusAdminToken = String.Empty;

        private JanusSession _JanusSession;
        private JanusAudioBridge _AudioBridge;

        // When connected to Janus, this is the room that is being used
        private JanusRoom _JanusRoom;

        public WebRtcJanusService(IConfigSource pConfig) : base(pConfig)
        {
            _log.DebugFormat("{0} WebRtcJanusService constructor", LogHeader);
            _Config = pConfig;
            IConfig webRtcVoiceConfig = _Config.Configs["WebRtcVoice"];

            if (webRtcVoiceConfig is not null)
            {
                _Enabled = webRtcVoiceConfig.GetBoolean("Enabled", false);
                IConfig janusConfig = _Config.Configs["JanusWebRtcVoice"];
                if (_Enabled && janusConfig is not null)
                {
                    _JanusServerURI = janusConfig.GetString("JanusGatewayURI", String.Empty);
                    _JanusAPIToken = janusConfig.GetString("APIToken", String.Empty);
                    _JanusAdminURI = janusConfig.GetString("JanusGatewayAdminURI", String.Empty);
                    _JanusAdminToken = janusConfig.GetString("AdminAPIToken", String.Empty);

                    if (String.IsNullOrEmpty(_JanusServerURI) || String.IsNullOrEmpty(_JanusAPIToken) ||
                        String.IsNullOrEmpty(_JanusAdminURI) || String.IsNullOrEmpty(_JanusAdminToken))
                    {
                        _log.ErrorFormat("{0} JanusWebRtcVoice configuration section missing required fields", LogHeader);
                        _Enabled = false;
                    }

                    if (_Enabled)
                    {
                        _log.DebugFormat("{0} Enabled", LogHeader);
                        StartConnectionToJanus();
                    }
                }
                else
                {
                    _log.ErrorFormat("{0} No JanusWebRtcVoice configuration section", LogHeader);
                    _Enabled = false;
                }
            }
            else
            {
                _log.ErrorFormat("{0} No WebRtcVoice configuration section", LogHeader);
                _Enabled = false;
            }
        }

        // Start a thread to do the connection to the Janus server.
        private void StartConnectionToJanus()
        {
            _log.DebugFormat("{0} StartConnectionToJanus", LogHeader);
            Task.Run(async () => 
            {
                _JanusSession = new JanusSession(_JanusServerURI, _JanusAPIToken, _JanusAdminURI, _JanusAdminToken);
                if (await _JanusSession.CreateSession())
                {
                    _log.DebugFormat("{0} JanusSession created", LogHeader);
                    // Once the session is created, create a handle to the plugin for rooms

                    _AudioBridge = new JanusAudioBridge(_JanusSession);
                    _JanusSession.AddPlugin(_AudioBridge);

                    if (await _AudioBridge.Activate(_Config))
                    {
                        _log.DebugFormat("{0} AudioBridgePluginHandle created", LogHeader);
                        // Requests through the capabilities will create rooms
                    }
                    else
                    {
                        _log.ErrorFormat("{0} JanusPluginHandle not created", LogHeader);
                    }
                }
                else
                {
                    _log.ErrorFormat("{0} JanusSession not created", LogHeader);
                }   
            });
        }

        // The pRequest parameter is a straight conversion of the JSON request from the client.
        // This is the logic that takes the client's request and converts it into
        //     operations on rooms in the audio bridge.
        // IWebRtcVoiceService.ProvisionVoiceAccountRequest
        public async Task<OSDMap> ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, IScene pScene)
        {
            OSDMap ret = null;
            string errorMsg = null;
            if (_AudioBridge is not null)
            {
                JanusViewerSession viewerSession = SetupViewerSession(pRequest);

                // TODO: check for logout=true
                // need to keep count of users in a room to know when to close a room
                bool isLogout = pRequest.ContainsKey("logout") && pRequest["logout"].AsBoolean();
                if (isLogout)
                {
                    // The client is logging out. Close the room
                    if (_JanusRoom is not null)
                    {
                        await _JanusRoom.LeaveRoom(viewerSession);
                        _JanusRoom = null;
                        return new OSDMap
                        {
                            { "response", "closed" }
                        };
                    }
                }

                // Get the parameters that select the room
                // To get here, voice_server_type has already been checked to be 'webrtc' and channel_type='local'
                int parcel_local_id = pRequest.ContainsKey("parcel_id") ? pRequest["parcel_id"].AsInteger() : JanusAudioBridge.REGION_ROOM_ID;
                string channel_id = pRequest.ContainsKey("channel_id") ? pRequest["channel_id"].AsString() : String.Empty;
                string channel_credentials = pRequest.ContainsKey("credentials") ? pRequest["credentials"].AsString() : String.Empty;
                string channel_type = pRequest["channel_type"].AsString();
                bool isSpacial = channel_type == "local";
                string voice_server_type = pRequest["voice_server_type"].AsString();

                _log.DebugFormat("{0} ProvisionVoiceAccountRequest: parcel_id={1} channel_id={2} channel_type={3} voice_server_type={4}", LogHeader, parcel_local_id, channel_id, channel_type, voice_server_type); 

                if (pRequest.ContainsKey("jsep") && pRequest["jsep"] is OSDMap jsep)
                {
                    // The jsep is the SDP from the client. This is the client's request to connect to the audio bridge.
                    string jsepType = jsep["type"].AsString();
                    string jsepSdp = jsep["sdp"].AsString();
                    if (jsepType == "offer")
                    {
                        _log.DebugFormat("{0} ProvisionVoiceAccountRequest: jsep type={1} sdp={2}", LogHeader, jsepType, jsepSdp);
                        _JanusRoom = await _AudioBridge.SelectRoom(channel_type, isSpacial, parcel_local_id, channel_id);
                        if (_JanusRoom is null)
                        {
                            errorMsg = "room selection failed";
                            _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: room selection failed", LogHeader);
                        }
                        else {
                            viewerSession.Room = _JanusRoom;
                            viewerSession.Offer = jsepSdp;
                            viewerSession.OfferOrig = jsepSdp;
                            viewerSession.AgentId = pUserID.ToString();
                            if (await _JanusRoom.JoinRoom(viewerSession))    
                            {
                                ret = new OSDMap
                                {
                                    { "jsep", viewerSession.Answer },
                                    { "viewer_session", viewerSession.SessionID }
                                };
                            }
                            else
                            {
                                errorMsg = "JoinRoom failed";
                                _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: JoinRoom failed", LogHeader);
                            }
                        }
                    }
                    else
                    {
                        errorMsg = "jsep type not offer";
                        _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: jsep type={1} not offer", LogHeader, jsepType);
                    }
                }
                else
                {
                    errorMsg = "no jsep";
                    _log.DebugFormat("{0} ProvisionVoiceAccountRequest: no jsep. req={1}", LogHeader, pRequest.ToString());
                }
            }
            else
            {
                errorMsg = "no JanusAudioBridge";
                _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: no JanusAudioBridge", LogHeader);
            }

            if (!String.IsNullOrEmpty(errorMsg) && ret is null)
            {
                // The provision failed so build an error messgage to return
                ret = new OSDMap
                {
                    { "response", "failed" },
                    { "error", errorMsg }
                };
            }

            return ret;
        }

        // IWebRtcVoiceService.VoiceAccountBalanceRequest
        public async Task<OSDMap> VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, IScene pScene)
        {
            OSDMap ret = null;
            if (_JanusSession is not null)
            {
                // The request should be an array of candidates
                if (pRequest.ContainsKey("candidate") && pRequest["candidate"] is OSDMap completed)
                {
                    if (completed.ContainsKey("completed") && completed["completed"].AsBoolean())
                    {
                        // The client has finished sending candidates
                        var candiateResp = await _JanusSession.PostToSession(new TrickleReq());
                        _log.DebugFormat("{0} VoiceSignalingRequest: candidate completed", LogHeader);
                    }
                }
                else
                {
                    if (pRequest.ContainsKey("candidates") && pRequest["candidates"] is OSDArray candidates)
                    {
                        OSDArray candidatesArray = new OSDArray();
                        foreach (OSDMap candidate in candidates)
                        {
                            candidatesArray.Add(new OSDMap() {
                                { "candidate", candidate["candidate"].AsString() },
                                { "sdpMid", candidate["sdpMid"].AsString() },
                                { "sdpMLineIndex", candidate["sdpMLineIndex"].AsLong() }
                            });
                        }
                        var candidatesResp = await _JanusSession.PostToSession(new TrickleReq(candidatesArray));
                    }
                    else
                    {
                        _log.ErrorFormat("{0} VoiceSignalingRequest: no candidates", LogHeader);
                    }
                }
            }
            return ret;
        }

        /// <summary>
        /// The session with the user is identified by the 'viewer_session' parameter in the message.
        /// Find the session or create a new one.
        /// </summary>
        /// <param name="pRequest"></param>
        /// <returns>found or created JanusViewerSession or 'null' if there was a session but it wasn't Janus</returns>
        private JanusViewerSession SetupViewerSession(OSDMap pRequest)
        {
            JanusViewerSession ret = null;
            if (pRequest.ContainsKey("viewer_session") && pRequest["viewer_session"] is OSDString vSession)
            {
                if (!WebRtcVoiceService.TryGetViewerSession<JanusViewerSession>(vSession, out ret))
                {
                    // 'viewer_session' is in the message but we don't have the tracker info for it
                    ret = new JanusViewerSession(vSession);
                    WebRtcVoiceService.AddViewerSession(ret);
                    pRequest["viewer_session"] = vSession;
                }
            }
            else
            {
                // No viewer session in the message. Create one
                ret = new JanusViewerSession(UUID.Random().ToString());
                pRequest["viewer_session"] = ret.SessionID;
                WebRtcVoiceService.AddViewerSession(ret);
            }
            return ret;
        }   
    }
 }
