﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using com.vtcsecure.ace.windows.CustomControls;
using com.vtcsecure.ace.windows.Model;
using com.vtcsecure.ace.windows.Services;
using com.vtcsecure.ace.windows.Utilities;
using com.vtcsecure.ace.windows.ViewModel;
using com.vtcsecure.ace.windows.Views;
using VATRP.Core.Events;
using VATRP.Core.Extensions;
using VATRP.Core.Model;
using VATRP.Core.Services;
using VATRP.Linphone.VideoWrapper;
using VATRP.LinphoneWrapper;
using VATRP.LinphoneWrapper.Enums;
using com.vtcsecure.ace.windows.CustomControls.UnifiedSettings;

namespace com.vtcsecure.ace.windows
{
	public partial class MainWindow
	{
		private bool registerRequested = false;
		private bool signOutRequest = false;
		private bool defaultConfigRequest;
        private object deferredLock = new object();
        private object regLock = new object();
        private System.Timers.Timer registrationTimer;
        private bool _isNeworkReachable;
	    private const int DECLINE_WAIT_TIMEOUT = 3000;
	    
	    private void DeferedHideOnError(object sender, EventArgs e)
	    {
	        lock (deferredLock)
	        {
	            deferredHideTimer.Stop();

	            if (_mainViewModel != null)
	            {
	                var viewModel = _mainViewModel.FindDeclinedCallViewModel();
	                if (viewModel != null)
	                {
	                    if (viewModel.WaitForDeclineMessage && viewModel.DeclinedMessage.NotBlank())
	                    {
	                        // restart timer
                            _mainViewModel.ActiveCallModel.WaitForDeclineMessage = false;
                            deferredHideTimer.Stop();
                            deferredHideTimer.Interval = TimeSpan.FromMilliseconds(DECLINE_WAIT_TIMEOUT);
                            deferredHideTimer.Start();
	                        return;
	                    }
	                    viewModel.DeclinedMessage = string.Empty;
	                    viewModel.ShowDeclinedMessage = false;
	                    if (_mainViewModel.ActiveCallModel.Equals(viewModel))
	                        _mainViewModel.ActiveCallModel = null;
	                    _mainViewModel.RemoveCalViewModel(viewModel);
	                }

	                if (ServiceManager.Instance.LinphoneService.GetActiveCallsCount == 0)
	                    ctrlCall.SetCallViewModel(null);
	                _mainViewModel.IsCallPanelDocked = false;
	            }
	        }
	    }
        private void DeferredShowPreview(object sender, EventArgs e)
        {
            deferredShowPreviewTimer.Stop();

            if (_selfView != null)
            {
                _selfView.Show();
                _selfView.Activate();
            }
        }
		
	    private void OnCallStateChanged(VATRPCall call)
	    {
	        if (this.Dispatcher == null)
	            return;

			if (this.Dispatcher.Thread != Thread.CurrentThread)
			{
				this.Dispatcher.BeginInvoke((Action)(() => this.OnCallStateChanged(call)));
				return;
			}

		    if (call == null)
		        return;

	        lock (deferredLock)
	        {
	            if (deferredHideTimer != null && deferredHideTimer.IsEnabled)
	                deferredHideTimer.Stop();
	        }

	        if (_mainViewModel == null)
	            return;

	        if (_mainViewModel.ActiveCallModel != null &&
	            _mainViewModel.ActiveCallModel.CallState == VATRPCallState.Declined)
	        {
                _mainViewModel.ActiveCallModel.DeclinedMessage = string.Empty;
                _mainViewModel.RemoveCalViewModel(_mainViewModel.ActiveCallModel);
	        }

			CallViewModel callViewModel = _mainViewModel.FindCallViewModel(call);

			if (callViewModel == null)
			{
				callViewModel = new CallViewModel(_linphoneService, call)
				{
					CallInfoCtrl = _callInfoView
				};

                callViewModel.HideMessageWindowTimeout += OnMessageHideTimeout;
			    callViewModel.CallQualityChangedEvent += OnCallQualityChanged;

                callViewModel.VideoWidth = (int)CombinedUICallViewSize.Width;
			    callViewModel.VideoHeight = (int)CombinedUICallViewSize.Height;
				_mainViewModel.AddCalViewModel(callViewModel);
			}

		    if ((call.CallState == VATRPCallState.InProgress) ||
                (call.CallState == VATRPCallState.StreamsRunning))
		    {
		        _mainViewModel.ActiveCallModel = callViewModel;
		    }

		    if (callViewModel.Declined)
		    {
                // do not process declined call
                _mainViewModel.RemoveCalViewModel(callViewModel);
		        return;
		    }

		    if (_mainViewModel.ActiveCallModel == null)
		        _mainViewModel.ActiveCallModel = callViewModel;

            LOG.Info(string.Format("CallStateChanged: State - {0}. Call: {1}", call.CallState, call.NativeCallPtr));
		    ctrlCall.SetCallViewModel(_mainViewModel.ActiveCallModel);
	        VATRPContact contact = null;

			var stopPlayback = false;
		    var destroycall = false;
	        var callDeclined = false;
            bool isError = false;
			switch (call.CallState)
			{
				case VATRPCallState.Trying:
					// call started, 
					call.RemoteParty = call.To;
					callViewModel.OnTrying();
			        _mainViewModel.IsCallPanelDocked = true;

                    if (callViewModel.Contact == null)
                        callViewModel.Contact = 
ServiceManager.Instance.ContactService.FindContact(new ContactID(string.Format("{0}@{1}", call.To.Username, call.To.HostAddress), IntPtr.Zero));

                    if (callViewModel.Avatar == null && callViewModel.Contact != null)
                    {
                        callViewModel.LoadAvatar(callViewModel.Contact.Avatar);
                    }

			        if (callViewModel.Contact == null)
			        {
			            var contactID = new ContactID(string.Format("{0}@{1}", call.To.Username, call.To.HostAddress),
			                IntPtr.Zero);
                        callViewModel.Contact = new VATRPContact(contactID)
                        {
                            DisplayName = call.To.DisplayName,
                            Fullname = call.To.Username,
                            SipUsername = call.To.Username,
                            RegistrationName = contactID.ID
                        };
			        }
                    
					break;
				case VATRPCallState.InProgress:
			        WakeupScreenSaver();
			        this.ShowSelfPreviewItem.IsEnabled = false;
					call.RemoteParty = call.From;
					ServiceManager.Instance.SoundService.PlayRingTone();
                    if (callViewModel.Contact == null)
                        callViewModel.Contact = 
ServiceManager.Instance.ContactService.FindContact(new ContactID(string.Format("{0}@{1}", call.From.Username, call.From.HostAddress), IntPtr.Zero));

                    if (callViewModel.Avatar == null && callViewModel.Contact != null)
                    {
                        callViewModel.LoadAvatar(callViewModel.Contact.Avatar);
                    }

			        if (callViewModel.Contact == null)
			        {
			            var contactID = new ContactID(string.Format("{0}@{1}", call.From.Username, call.From.HostAddress),
			                IntPtr.Zero);
                        callViewModel.Contact = new VATRPContact(contactID)
                        {
                            DisplayName = call.From.DisplayName,
                            Fullname = call.From.Username,
                            SipUsername = call.From.Username,
                            RegistrationName = contactID.ID
                        };
			        }

                    callViewModel.OnIncomingCall();

			        if (_linphoneService.GetActiveCallsCount == 2)
			        {
			            ShowOverlayNewCallWindow(true);
                        ctrlCall.ctrlOverlay.SetNewCallerInfo(callViewModel.CallerInfo);
			        }
			        else
			        {
			            callViewModel.ShowIncomingCallPanel = true;
			        }

                    if (WindowState != WindowState.Minimized)
                        _mainViewModel.IsCallPanelDocked = true;
                    
                    if (_flashWindowHelper != null)
                        _flashWindowHelper.FlashWindow(this);
			        if (WindowState == WindowState.Minimized)
                        this.WindowState = WindowState.Normal;
                    
                    Topmost = true;
                    Activate();
                    Topmost = false;
			        break;
				case VATRPCallState.Ringing:
                    this.ShowSelfPreviewItem.IsEnabled = false;
					callViewModel.OnRinging();
                    _mainViewModel.IsCallPanelDocked = true;
					call.RemoteParty = call.To;
					ctrlCall.ctrlOverlay.SetCallerInfo(callViewModel.CallerInfo);
					ctrlCall.ctrlOverlay.SetCallState("Ringing");
                    if (callViewModel.Contact == null)
                        callViewModel.Contact = 
ServiceManager.Instance.ContactService.FindContact(new ContactID(string.Format("{0}@{1}", call.To.Username, call.To.HostAddress), IntPtr.Zero));

                    if (callViewModel.Avatar == null && callViewModel.Contact != null)
                    {
                        callViewModel.LoadAvatar(callViewModel.Contact.Avatar);
                    }

			        if (callViewModel.Contact == null)
			        {
			            var contactID = new ContactID(string.Format("{0}@{1}", call.To.Username, call.To.HostAddress),
			                IntPtr.Zero);
                        callViewModel.Contact = new VATRPContact(contactID)
                        {
                            DisplayName = call.To.DisplayName,
                            Fullname = call.To.Username,
                            SipUsername = call.To.Username,
                            RegistrationName = contactID.ID
                        };
			        }

					ServiceManager.Instance.SoundService.PlayRingBackTone();
					break;
				case VATRPCallState.EarlyMedia:
					callViewModel.OnEarlyMedia();
					break;
				case VATRPCallState.Connected:
			        if (ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
			            Configuration.ConfEntry.USE_RTT, true))
			        {
			            _mainViewModel.IsRTTViewEnabled = true;
			            ctrlRTT.SetViewModel(_mainViewModel.RttMessagingModel);
			            _mainViewModel.RttMessagingModel.CreateRttConversation(call.RemoteParty.Username, call.NativeCallPtr);
			        }
			        else
			        {
                        _mainViewModel.IsRTTViewEnabled = false;
			        }

					callViewModel.OnConnected();
                    if (_flashWindowHelper != null)
                        _flashWindowHelper.StopFlashing();
					stopPlayback = true;
					callViewModel.ShowOutgoingEndCall = false;
			        callViewModel.IsRTTEnabled =
			            ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
			                Configuration.ConfEntry.USE_RTT, true) && callViewModel.ActiveCall != null &&
			            _linphoneService.IsRttEnabled(callViewModel.ActiveCall.NativeCallPtr);

					ShowCallOverlayWindow(true);
                    ShowOverlayNewCallWindow(false);
					ctrlCall.ctrlOverlay.SetCallerInfo(callViewModel.CallerInfo);
					ctrlCall.ctrlOverlay.SetCallState("Connected");
                    ctrlCall.ctrlOverlay.ForegroundCallDuration = callViewModel.CallDuration;

			        if (_linphoneService.GetActiveCallsCount == 2)
			        {
			            CallViewModel nextVM = _mainViewModel.GetNextViewModel(callViewModel);
			            if (nextVM != null)
			            {
			                ShowOverlaySwitchCallWindow(true);
			                ctrlCall.ctrlOverlay.SetPausedCallerInfo(nextVM.CallerInfo);
                            ctrlCall.ctrlOverlay.BackgroundCallDuration = nextVM.CallDuration;
                            ctrlCall.ctrlOverlay.StartPausedCallTimer(ctrlCall.ctrlOverlay.BackgroundCallDuration);
                            ctrlCall.BackgroundCallViewModel = nextVM;
			            }
			        }
			        else
			        {
                        ShowOverlaySwitchCallWindow(false);
			        }

                    ctrlCall.ctrlOverlay.StartCallTimer(ctrlCall.ctrlOverlay.ForegroundCallDuration);
					_callOverlayView.EndCallRequested = false;

                    if (_selfView.IsVisible)
                    {
                        _selfView.ResetNativePreviewHandle = false;
                        _selfView.Hide();
                    }

                    ctrlCall.ctrlVideo.DrawCameraImage = false;
					ctrlCall.AddVideoControl();
                    ctrlCall.RestartInactivityDetectionTimer();
			        ctrlCall.UpdateVideoSettingsIfOpen();
                    ctrlCall.UpdateMuteSettingsIfOpen();

//                    MuteCall(createCmd.MuteMicrophone);
//                    MuteSpeaker(createCmd.MuteSpeaker);

					break;
				case VATRPCallState.StreamsRunning:
					callViewModel.OnStreamRunning();
                    ShowCallOverlayWindow(true);

                    // VATRP-1623: we are setting mute microphone true prior to initiating a call, but the call is always started
                    //   with the mic enabled. attempting to mute right after call is connected here to side step this issue - 
                    //   it appears to be an initialization issue in linphone
                    if (_linphoneService.GetActiveCallsCount == 1)
                    {
                        ServiceManager.Instance.ApplyMediaSettingsChanges();
                    }
                    ctrlCall.ctrlOverlay.SetCallState("Connected");
			        ctrlCall.UpdateControls();
                    ctrlCall.ctrlOverlay.ForegroundCallDuration = _mainViewModel.ActiveCallModel.CallDuration;
                    ctrlCall.RestartInactivityDetectionTimer();
			        ctrlCall.UpdateVideoSettingsIfOpen();
                    // VATRP-1899: This is a quick and dirty solution for POC. It will be funational, but not the end implementation we will want.
                    if ((App.CurrentAccount != null) && App.CurrentAccount.UserNeedsAgentView)
                    {
                        _mainViewModel.IsMessagingDocked = true;
                    }
					break;
				case VATRPCallState.RemotePaused:
			        callViewModel.OnRemotePaused();
                    callViewModel.CallState = VATRPCallState.RemotePaused;
                    ShowCallOverlayWindow(true);
                    ctrlCall.ctrlOverlay.SetCallerInfo(callViewModel.CallerInfo);
                    ctrlCall.ctrlOverlay.SetCallState("On Hold");
                    ctrlCall.UpdateControls();
					break;
                case VATRPCallState.LocalPausing:
                    callViewModel.CallState = VATRPCallState.LocalPausing;
                    break;
                case VATRPCallState.LocalPaused:
                    callViewModel.OnLocalPaused();
                    callViewModel.CallState = VATRPCallState.LocalPaused;
			        callViewModel.IsCallOnHold = true;
			        bool updateInfoView = callViewModel.PauseRequest;
			        if (_linphoneService.GetActiveCallsCount == 2)
			        {
			            if (!callViewModel.PauseRequest)
			            {
			                CallViewModel nextVM = _mainViewModel.GetNextViewModel(callViewModel);

			                if (nextVM != null)
			                {
			                    ShowOverlaySwitchCallWindow(true);
			                    ctrlCall.ctrlOverlay.SetPausedCallerInfo(callViewModel.CallerInfo);
			                    ctrlCall.ctrlOverlay.BackgroundCallDuration = callViewModel.CallDuration;
			                    ctrlCall.ctrlOverlay.StartPausedCallTimer(ctrlCall.ctrlOverlay.BackgroundCallDuration);
			                    ctrlCall.BackgroundCallViewModel = callViewModel;
			                    ctrlCall.ctrlOverlay.ForegroundCallDuration = nextVM.CallDuration;
			                    ctrlCall.SetCallViewModel(nextVM);
			                    if (!nextVM.PauseRequest)
			                        _mainViewModel.ResumeCall(nextVM);
			                    else
			                        updateInfoView = true;
			                }
			            }
			        }

                    if (updateInfoView)
			        {
                        ShowCallOverlayWindow(true);
                        ctrlCall.ctrlOverlay.SetCallerInfo(callViewModel.CallerInfo);
                        ctrlCall.ctrlOverlay.SetCallState("On Hold");
                        ctrlCall.UpdateControls();
			        }
			        break;
                case VATRPCallState.LocalResuming:
                    callViewModel.OnResumed();
                    callViewModel.IsCallOnHold = false;
                    ShowCallOverlayWindow(true);
					ctrlCall.ctrlOverlay.SetCallerInfo(callViewModel.CallerInfo);
					ctrlCall.ctrlOverlay.SetCallState("Connected");
			        ctrlCall.UpdateControls();

			        if (_linphoneService.GetActiveCallsCount == 2)
			        {
			            CallViewModel nextVM = _mainViewModel.GetNextViewModel(callViewModel);
			            if (nextVM != null)
			            {
			                ShowOverlaySwitchCallWindow(true);
			                ctrlCall.ctrlOverlay.SetPausedCallerInfo(nextVM.CallerInfo);
                            ctrlCall.ctrlOverlay.BackgroundCallDuration = nextVM.CallDuration ;
                            ctrlCall.ctrlOverlay.StartPausedCallTimer(ctrlCall.ctrlOverlay.BackgroundCallDuration);
                            ctrlCall.BackgroundCallViewModel = nextVM;
                            ctrlCall.SetCallViewModel(callViewModel);
                            ctrlCall.ctrlOverlay.ForegroundCallDuration = callViewModel.CallDuration;
                            if (ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
                        Configuration.ConfEntry.USE_RTT, true))
                            {
                                _mainViewModel.RttMessagingModel.CreateRttConversation(call.RemoteParty.Username, call.NativeCallPtr);
                            }
			            }
			            else
			            {
			                ShowOverlaySwitchCallWindow(false);
			            }
			        }
			        else
			        {
			            ShowOverlaySwitchCallWindow(false);
			        }
                    ctrlCall.ctrlVideo.DrawCameraImage = false;
					ctrlCall.AddVideoControl();
                    break;
                case VATRPCallState.Closed:
					if (_flashWindowHelper != null)
                        _flashWindowHelper.StopFlashing();
                    LOG.Info(string.Format("CallStateChanged: Result Code - {0}. Message: {1} Call: {2}", call.SipErrorCode, call.LinphoneMessage, call.NativeCallPtr));
			        callDeclined = call.SipErrorCode == 603;
                    callViewModel.OnClosed(ref isError, call.LinphoneMessage, call.SipErrorCode, callDeclined);
					stopPlayback = true;
			        destroycall = true;
                    callViewModel.CallQualityChangedEvent -= OnCallQualityChanged;
                    if (ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
                       Configuration.ConfEntry.USE_RTT, true))
                    {
                        _mainViewModel.RttMessagingModel.ClearRTTConversation(call.NativeCallPtr);
                        ctrlRTT.SetViewModel(null);
                    }
                    ShowOverlayNewCallWindow(false);
                    ShowOverlaySwitchCallWindow(false);
                    ctrlCall.BackgroundCallViewModel = null;

			        if (callDeclined)
			        {
                        _mainViewModel.IsRTTViewEnabled = false;
                        this.ShowSelfPreviewItem.IsEnabled = true;
                        _callInfoView.Hide();
                        ctrlCall.ctrlOverlay.StopCallTimer();
                        ShowCallOverlayWindow(false);
                        _mainViewModel.IsMessagingDocked = false;
			            if (_mainViewModel.ActiveCallModel.DeclinedMessage.NotBlank())
			                _mainViewModel.ActiveCallModel.ShowDeclinedMessage = true;
			            else
			            {
                            _mainViewModel.ActiveCallModel.WaitForDeclineMessage = true;
			            }
			            if (deferredHideTimer != null)
			            {
			                lock (deferredLock)
			                {
                                deferredHideTimer.Interval = TimeSpan.FromMilliseconds(DECLINE_WAIT_TIMEOUT);
			                    deferredHideTimer.Start();
			                }
			            }
			        }
			        else
			        {
			            int callsCount = _mainViewModel.RemoveCalViewModel(callViewModel);
			            if (callsCount == 0)
			            {
			                _mainViewModel.IsRTTViewEnabled = false;
			                this.ShowSelfPreviewItem.IsEnabled = true;
			                _callInfoView.Hide();
			                ctrlCall.ctrlOverlay.StopCallTimer();
			                
			                if (!isError)
			                {
                                this.SizeToContent = SizeToContent.WidthAndHeight;
                                ctrlCall.SetCallViewModel(null);
                                _mainViewModel.IsCallPanelDocked = false;
			                }
			                else
			                {
                                if (deferredHideTimer != null)
                                {
                                    lock (deferredLock)
                                    {
                                        deferredHideTimer.Interval = TimeSpan.FromMilliseconds(DECLINE_WAIT_TIMEOUT);
                                        deferredHideTimer.Start();
                                    }
                                }
			                }
                            ShowCallOverlayWindow(false);
                            _mainViewModel.IsMessagingDocked = false;
			                _mainViewModel.ActiveCallModel = null;
			                OnFullScreenToggled(false); // restore main window to dashboard

			                if (this.ShowSelfPreviewItem.IsChecked && !_selfView.ResetNativePreviewHandle)
			                {
			                    _selfView.ResetNativePreviewHandle = true;
			                    deferredShowPreviewTimer.Start();
			                }
			            }
			            else
			            {
			                var nextVM = _mainViewModel.GetNextViewModel(null);
			                if (nextVM != null)
			                {
			                    // defensive coding here- do not try to operate on an errored call state object
			                    if (nextVM.CallState != VATRPCallState.Error)
			                    {
			                        _mainViewModel.ActiveCallModel = nextVM;
			                        nextVM.CallSwitchLastTimeVisibility = Visibility.Hidden;

			                        if (ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
			                            Configuration.ConfEntry.USE_RTT, true))
			                        {
			                            _mainViewModel.IsRTTViewEnabled = true;
			                            ctrlRTT.SetViewModel(_mainViewModel.RttMessagingModel);
			                            _mainViewModel.RttMessagingModel.CreateRttConversation(
			                                nextVM.ActiveCall.RemoteParty.Username, nextVM.ActiveCall.NativeCallPtr);
			                        }
			                        else
			                        {
			                            _mainViewModel.IsRTTViewEnabled = false;
			                        }
			                        ShowCallOverlayWindow(true);
			                        ctrlCall.ctrlOverlay.SetCallerInfo(nextVM.CallerInfo);
			                        ctrlCall.ctrlOverlay.ForegroundCallDuration = _mainViewModel.ActiveCallModel.CallDuration;
			                        ctrlCall.SetCallViewModel(_mainViewModel.ActiveCallModel);
			                        ctrlCall.UpdateControls();
			                        if (nextVM.ActiveCall.CallState == VATRPCallState.LocalPaused)
			                        {
			                            if (!nextVM.PauseRequest)
			                                _mainViewModel.ResumeCall(nextVM);
			                            else
			                            {
			                                ctrlCall.ctrlOverlay.SetCallState("On Hold");
			                            }
			                        }
			                    }
			                }
			            }
			        }
			        if ((registerRequested || signOutRequest || defaultConfigRequest) && _linphoneService.GetActiveCallsCount == 0)
					{
						_linphoneService.Unregister(false);
					}

					break;
				case VATRPCallState.Error:
			        destroycall = true;
			        if (_flashWindowHelper != null) 
                        _flashWindowHelper.StopFlashing();
			        ctrlCall.BackgroundCallViewModel = null;
			        isError = true;
                    callViewModel.OnClosed(ref isError, call.LinphoneMessage, call.SipErrorCode, false);
                    callViewModel.CallSwitchLastTimeVisibility = Visibility.Hidden;
					stopPlayback = true;
                    if (ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
                       Configuration.ConfEntry.USE_RTT, true))
                    {
                        _mainViewModel.RttMessagingModel.ClearRTTConversation(call.NativeCallPtr);
                        ctrlRTT.SetViewModel(null);
                    }

					if (_linphoneService.GetActiveCallsCount == 0)
					{
                        _mainViewModel.IsRTTViewEnabled = false;
                        this.ShowSelfPreviewItem.IsEnabled = true;
                        if (this.ShowSelfPreviewItem.IsChecked && !_selfView.ResetNativePreviewHandle)
                        {
                            _selfView.ResetNativePreviewHandle = true;
                            deferredShowPreviewTimer.Start();
                        }
					    if (callViewModel.CallState != VATRPCallState.Declined)
					        _mainViewModel.RemoveCalViewModel(callViewModel);
                        _callInfoView.Hide();
						ctrlCall.ctrlOverlay.StopCallTimer();
                        ShowCallOverlayWindow(false);
                        _mainViewModel.IsMessagingDocked = false;

					    if (deferredHideTimer != null)
					    {
					        lock (deferredLock)
					        {
                                deferredHideTimer.Interval = TimeSpan.FromMilliseconds(DECLINE_WAIT_TIMEOUT);
					            deferredHideTimer.Start();
					        }
					    }

					    _mainViewModel.ActiveCallModel = null;
                        OnFullScreenToggled(false); // restore main window to dashboard
					}
                    else
                    {
                        _mainViewModel.RemoveCalViewModel(callViewModel);
                    }
					
					break;
				default:
					break;
			}

			if (stopPlayback)
			{
				ServiceManager.Instance.SoundService.StopRingBackTone();
				ServiceManager.Instance.SoundService.StopRingTone();
			}

		    if (destroycall)
		    {
                if (_linphoneService.GetActiveCallsCount == 0)
                {
                    if (_mainViewModel.IsCallPanelDocked)
                    {
                        ServiceManager.Instance.LinphoneService.SetVideoCallWindowHandle(IntPtr.Zero, true);
                        if (_remoteVideoView != null)
                        {
                            _remoteVideoView.DestroyOnClosing = true; // allow window to be closed
                            _remoteVideoView.Close();
                            _remoteVideoView = null;
                        }
                        if (deferredHideTimer != null && !deferredHideTimer.IsEnabled)
                        {
                            _mainViewModel.IsMessagingDocked = false;
                            _mainViewModel.IsCallPanelDocked = false;
                        }
                    }

                    if (!callDeclined)
                        _mainViewModel.ActiveCallModel = null;
                }
		    }
		}

        private void OnMessageHideTimeout(object sender, EventArgs e)
        {
            if (this.Dispatcher == null)
                return;

            if (this.Dispatcher.Thread != Thread.CurrentThread)
            {
                this.Dispatcher.BeginInvoke((Action)(() => this.OnMessageHideTimeout(sender, e)));
                return;
            }

            var callViewModel = sender as CallViewModel;
            if (callViewModel != null && callViewModel.ShowInfoMessage)
            {
                _mainViewModel.ActiveCallModel.ShowInfoMessage = false;
                ctrlCall.ctrlOverlay.ShowInfoMsgWindow(false);
            }
        }

        private void WakeupScreenSaver()
        {
            // simulate mouse move event
            ScreenSaverHelper.SimulateMouseMoveEvent(this);

            if (!ScreenSaverHelper.IsScreenSaverActive())
                return;

            if (!ScreenSaverHelper.IsScreenSaverRunning())
                return;
            ScreenSaverHelper.KillScreenSaver();
        }

        private void OnCallQualityChanged(VATRP.Linphone.VideoWrapper.QualityIndicator callQuality)
        {
            if (_mainViewModel.ActiveCallModel == null || 
                callQuality == VATRP.Linphone.VideoWrapper.QualityIndicator.Unknown)
            {
                ctrlCall.ctrlOverlay.ShowQualityIndicatorWindow(false);
                ctrlCall.ctrlOverlay.ShowEncryptionIndicatorWindow(false);
                return;
            }

            ctrlCall.ctrlOverlay.UpdateQualityIndicator(callQuality);
            ctrlCall.ctrlOverlay.ShowQualityIndicatorWindow(this.WindowState != WindowState.Minimized);


            if (_mainViewModel.ActiveCallModel.CallInfoModel != null)
            {
                var encryption = _mainViewModel.ActiveCallModel.CallInfoModel.MediaEncryption.Equals("None") ? MediaEncryptionIndicator.Off
                    : MediaEncryptionIndicator.On;
                ctrlCall.ctrlOverlay.UpdateEncryptionIndicator(encryption);
                ctrlCall.ctrlOverlay.ShowEncryptionIndicatorWindow(this.WindowState != WindowState.Minimized);

                if (_mainViewModel.ActiveCallModel.ShowInfoMessage)
                {
                    ctrlCall.ctrlOverlay.ShowInfoMsgWindow(this.WindowState != WindowState.Minimized);
                }
            }
        }

	    private void OnSwitchHoldCallsRequested(object sender, EventArgs eventArgs)
	    {
	        if (_linphoneService.GetActiveCallsCount != 2)
	            return;

	        CallViewModel callViewModel = ctrlCall.BackgroundCallViewModel;
            CallViewModel nextVM = _mainViewModel.GetNextViewModel(callViewModel);
	        if (nextVM != null)
	        {
	            ShowOverlaySwitchCallWindow(true);
	            ctrlCall.ctrlOverlay.SetPausedCallerInfo(nextVM.CallerInfo);
	            ctrlCall.ctrlOverlay.BackgroundCallDuration = nextVM.CallDuration;
	            ctrlCall.ctrlOverlay.StartPausedCallTimer(ctrlCall.ctrlOverlay.BackgroundCallDuration);
	            ctrlCall.BackgroundCallViewModel = nextVM;
	            ctrlCall.SetCallViewModel(callViewModel);
                ctrlCall.ctrlOverlay.SetCallerInfo(callViewModel.CallerInfo);
	            ctrlCall.ctrlOverlay.ForegroundCallDuration = callViewModel.CallDuration;
	            if (ServiceManager.Instance.ConfigurationService.Get(Configuration.ConfSection.GENERAL,
	                Configuration.ConfEntry.USE_RTT, true))
	            {
	                _mainViewModel.RttMessagingModel.CreateRttConversation(callViewModel.ActiveCall.RemoteParty.Username,
	                    callViewModel.ActiveCall.NativeCallPtr);
	            }
	        }
	        else
	        {
	            ShowOverlaySwitchCallWindow(false);
	        }
	    }

	    private void ShowCallOverlayWindow(bool bShow)
	    {
	        bShow &= (this.WindowState != WindowState.Minimized);
            if (bShow)
                RearrangeUICallView(GetCallViewSize());

			ctrlCall.ctrlOverlay.ShowCommandBar(bShow);
			ctrlCall.ctrlOverlay.ShowNumpadWindow(false);
			ctrlCall.ctrlOverlay.ShowCallInfoWindow(bShow);
	        ctrlCall.ctrlOverlay.ShowQualityIndicatorWindow(bShow);
            ctrlCall.ctrlOverlay.ShowEncryptionIndicatorWindow(bShow);
		    if (!bShow)
		    {
		        ctrlCall.ctrlVideo.Visibility = Visibility.Hidden;
                ctrlCall.ctrlOverlay.ShowNewCallAcceptWindow(false);
                ctrlCall.ctrlOverlay.ShowCallsSwitchWindow(false);
                ctrlCall.ctrlOverlay.ShowOnHoldWindow(false);
                ctrlCall.ctrlOverlay.ShowQualityIndicatorWindow(false);
                ctrlCall.ctrlOverlay.ShowEncryptionIndicatorWindow(false);
                ctrlCall.ctrlOverlay.ShowInfoMsgWindow(false);
            }
		}

        private void ShowOverlayNewCallWindow(bool bShow)
        {
            ctrlCall.ctrlOverlay.ShowNewCallAcceptWindow(bShow);
        }
        private void ShowOverlaySwitchCallWindow(bool bShow)
        {
            ctrlCall.ctrlOverlay.ShowCallsSwitchWindow(bShow);
            if (!bShow)
                ctrlCall.ctrlOverlay.StopPausedCallTimer();
        }

		private void OnRegistrationChanged(LinphoneRegistrationState state, LinphoneReason reason)
		{
		    if (RegistrationState == state)
		        return;

			if (this.Dispatcher.Thread != Thread.CurrentThread)
			{
                this.Dispatcher.BeginInvoke((Action)(() => this.OnRegistrationChanged(state, reason)));
				return;
			}
            LOG.Info(String.Format("Registration state changed from {0} to {1}. Reason: {2}", RegistrationState, state, reason));
            var processSignOut = false;
            RegistrationState = state;
		    RegistrationFailReason = reason;

            this.BtnMoreMenu.IsEnabled = true;
            _mainViewModel.ContactModel.RegistrationState = state;
			switch (state)
			{
				case LinphoneRegistrationState.LinphoneRegistrationProgress:
					this.BtnMoreMenu.IsEnabled = false;
                    // VATRP-3225: here we want to kick off a timer - if we do not get the OK in a reasonable amount of time, then 
                    //   we need to doa reset - preferably behind the scenes - and try to log in again.
			        lock (regLock)
			        {
			            if (registrationTimer == null)
			            {
			                registrationTimer = new System.Timers.Timer();
			                registrationTimer.Elapsed += new System.Timers.ElapsedEventHandler(RegistrationTimerTick);
			                registrationTimer.Interval = 30000;
			                registrationTimer.Enabled = true;
			                registrationTimer.Start();
			            }
			        }
			        return;
				case LinphoneRegistrationState.LinphoneRegistrationOk:
                    DestroyRegistrationTimer();

                    _playRegistrationFailureNotify = true;
                    if (_playRegisterNotify)
			        {
			            _playRegisterNotify = false;
			            ServiceManager.Instance.SoundService.PlayConnectionChanged(true);
			        }
                    signOutRequest = false;
					break;
                case LinphoneRegistrationState.LinphoneRegistrationNone:
                case LinphoneRegistrationState.LinphoneRegistrationFailed:
                    DestroyRegistrationTimer();

			        if (state == LinphoneRegistrationState.LinphoneRegistrationFailed)
			        {
                        signOutRequest = false;
			            _playRegisterNotify = true;
                        if (_playRegistrationFailureNotify)
			            {
			                ServiceManager.Instance.SoundService.PlayConnectionChanged(false);
                            _playRegistrationFailureNotify = false;
			            }
			            lock (regLock)
			            {
			                registrationTimer = new System.Timers.Timer();
			                registrationTimer.Elapsed += new System.Timers.ElapsedEventHandler(RegistrationTimerTick);
			                registrationTimer.Interval = 120000;
			                registrationTimer.Start();
			            }
			            Debug.WriteLine("Start register retry timer: ");
			        }
                    break;
				case LinphoneRegistrationState.LinphoneRegistrationCleared:
                    DestroyRegistrationTimer();

					ServiceManager.Instance.SoundService.PlayConnectionChanged(false);
			        _playRegisterNotify = true;
			        _playRegistrationFailureNotify = false;
					if (registerRequested)
					{
						registerRequested = false;
						_linphoneService.Register();
					}
					else if (signOutRequest || defaultConfigRequest)
					{
					    processSignOut = true;
                    }
			        
					break;
				default:
					break;
			}
            UpdateMenuSettingsForRegistrationState();
		    if (processSignOut)
		    {
		        ProceedToLoginPage();
		        signOutRequest = false;
		    }
		}

	    private void ProceedToLoginPage()
	    {
// Liz E. note: we want to perfomr different actions for logout and default configuration request.
	        // If we are just logging out, then we need to not clear the account, just the password, 
	        // and jump to the second page of the wizard.
	        if (!_mainViewModel.IsAccountLogged)
	            return;

	        WizardPagepanel.Children.Clear();
	        // VATRP - 1325, Go directly to VRS page
	        _mainViewModel.OfferServiceSelection = false;
	        _mainViewModel.ActivateWizardPage = true;

	        signOutRequest = false;
	        _mainViewModel.IsAccountLogged = false;
	        CloseDialpadAnimated();
	        _mainViewModel.IsCallHistoryDocked = false;
	        _mainViewModel.IsContactDocked = false;
	        _mainViewModel.IsMessagingDocked = false;

	        if (defaultConfigRequest)
	        {
	            ServiceManager.Instance.ConfigurationService.Set(Configuration.ConfSection.GENERAL,
	                Configuration.ConfEntry.ACCOUNT_IN_USE, string.Empty);

	            defaultConfigRequest = false;
	            ServiceManager.Instance.AccountService.DeleteAccount(App.CurrentAccount);
	            ResetConfiguration();
	            App.CurrentAccount = null;
	            // VATRP - 1325, Go directly to VRS page
	            OnVideoRelaySelect(this, null);
	        }
	        else
	        {
	            this.Wizard_HandleLogout();
	        }
	        ServiceManager.Instance.ContactService.Stop();
	        ServiceManager.Instance.HistoryService.Stop();
	        ServiceManager.Instance.ChatService.Stop();

	        // stop linphone to force contacts list reload
	        ServiceManager.Instance.LinphoneService.Stop();

	        // hide messaging window
	        if (_messagingWindow.IsVisible)
	        {
	            _messagingWindow.Hide();
	            ShowMessagingViewItem.IsChecked = false;
	        }
	        signOutRequest = false;
	    }

	    private void RegistrationTimerTick(object source, System.Timers.ElapsedEventArgs e)
	    {
	        bool register = registrationTimer.Interval == 120000;
            DestroyRegistrationTimer();
            // VATRP-3225 - if we get here, then we recieved a registration state of Progress without a followup change. We need to invalidate and try to register again.
            //   This is a workaround - I do not prefer this, would rather have the solution, be we need answers as to what is causing the state in order to fix it properly.
            if (RegistrationState == LinphoneRegistrationState.LinphoneRegistrationProgress)
            {
                // then we need to stop linphone and restart it, then send a registration request with the current information
//                ServiceManager.Instance.ContactService.Stop();
//                ServiceManager.Instance.HistoryService.Stop();
//                ServiceManager.Instance.ChatService.Stop();

                // stop linphone to force contacts list reload
//                ServiceManager.Instance.LinphoneService.Stop();
                registerRequested = true;
                _linphoneService.Unregister(false);
            } 
            else if (register)
            {
                registerRequested = true;
                _linphoneService.Register();
            }
        }

		private void ResetConfiguration()
		{
			ServiceManager.Instance.AccountService.ClearAccounts();
			ServiceManager.Instance.ConfigurationService.Reset();   
			ServiceManager.Instance.HistoryService.ClearCallsItems();
			ServiceManager.Instance.ContactService.RemoveContacts();
			defaultConfigRequest = false;
		}

        // VATRP-1899: This is a quick and dirty solution for POC. It will be funational, but not the end implementation we will want.
        private void SetToUserAgentView(bool isUserAgent)
        {
            System.Windows.Visibility visibility = System.Windows.Visibility.Visible;

            if (isUserAgent)
            {
                visibility = System.Windows.Visibility.Collapsed; 
            }
            // for the agent view, we want to hide all navigation buttons except the settings button.
            // we want to ensure that auto answer is set to true.
            this.BtnContacts.Visibility = visibility;
            this.BtnDialpad.Visibility = visibility;
            this.BtnRecents.Visibility = visibility;
            this.BtnChatView.Visibility = visibility;
            this.BtnMoreMenu.Visibility = System.Windows.Visibility.Visible;
            // configure the other settings that we need for user agent:
        }

		private void OnNewAccountRegistered(string accountId)
		{
		    _mainViewModel.OfferServiceSelection = false;
		    _mainViewModel.ActivateWizardPage = false;
			LOG.Info(string.Format( "New account registered. Username -{0}. Host - {1} Port - {2}",
				App.CurrentAccount.RegistrationUser,
				App.CurrentAccount.ProxyHostname,
				App.CurrentAccount.ProxyPort));

            _mainViewModel.IsAccountLogged = true;
            // VATRP-1899: This is a quick and dirty solution for POC. It will be funational, but not the end implementation we will want.
            if ((App.CurrentAccount != null) && (!App.CurrentAccount.UserNeedsAgentView))
            {
                OpenDialpadAnimated();
                UpdateVideomailCount();

                _mainViewModel.IsCallHistoryDocked = true;
                _mainViewModel.IsContactDocked = false;
                _mainViewModel.IsSettingsDocked = false;
                _mainViewModel.IsResourceDocked = false;
                _mainViewModel.IsMenuDocked = false;

                _mainViewModel.DialpadModel.UpdateProvider();
                SetToUserAgentView(false);
            }
            else
            {
                SetToUserAgentView(true);
            }
            this.SelfViewItem.IsChecked = App.CurrentAccount.ShowSelfView;
            ServiceManager.Instance.UpdateLoggedinContact();
		    ServiceManager.Instance.StartupLinphoneCore();
		}

	    private void UpdateVideomailCount()
	    {
	        if (_mainViewModel.ContactModel != null)
	            _mainViewModel.ContactModel.VideoMailCount = App.CurrentAccount.VideoMailCount;
	        if (_mainViewModel.MoreMenuModel != null)
	            _mainViewModel.MoreMenuModel.VideoMailCount = App.CurrentAccount.VideoMailCount;
	        _mainViewModel.ShowVideomailIndicator = App.CurrentAccount.VideoMailCount > 0;
	    }

	    private void OnChildVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (App.AllowDestroyWindows)
				return;
			var window = sender as VATRPWindow;
			if (window == null)
				return;
			var bShow = (bool)e.NewValue;
			switch (window.WindowType)
			{
				case VATRPWindowType.CONTACT_VIEW:
					BtnContacts.IsChecked = bShow;
					break;
				case VATRPWindowType.MESSAGE_VIEW:
			        if (this.ShowMessagingViewItem.IsEnabled)
			        {
			            this.ShowMessagingViewItem.IsChecked = bShow;
			            _mainViewModel.IsChatViewEnabled = bShow;
			        }
			        break;
				case VATRPWindowType.RECENTS_VIEW:
					BtnRecents.IsChecked = bShow;
					_mainViewModel.IsCallHistoryDocked = !bShow;
					break;
				case VATRPWindowType.DIALPAD_VIEW:
					BtnDialpad.IsChecked = bShow;
					_mainViewModel.IsDialpadDocked = !bShow;
					break;
				case VATRPWindowType.SETTINGS_VIEW:
					break;
                case VATRPWindowType.SELF_VIEW:
			        if (this.ShowSelfPreviewItem.IsEnabled)
			        {
			            this.ShowSelfPreviewItem.IsChecked = bShow;
			            _mainViewModel.MoreMenuModel.IsSelfViewActive = bShow;
			        }
			        break;
			}
		}

		private void OnCallInfoVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (App.AllowDestroyWindows)
				return;
			var window = sender as VATRPWindow;
			if (window == null)
				return;
			var bShow = (bool)e.NewValue;
			if (_mainViewModel.ActiveCallModel != null)
			{
				_mainViewModel.ActiveCallModel.IsCallInfoOn = bShow;
			}

			if (ctrlCall != null) 
				ctrlCall.BtnInfo.IsChecked = bShow;
		}

		private void OnMakeCallRequested(string called_address)
		{
		    lock (_mainViewModel.CallsViewModelList)
		    {
		        if (_mainViewModel.CallsViewModelList.Count > 0)
		            return;
		    }

		    _mainViewModel.DialpadModel.RemotePartyNumber = "";
			MediaActionHandler.MakeVideoCall(called_address);
		}

		private void OnKeypadClicked(object sender, KeyPadEventArgs e)
		{
			_linphoneService.PlayDtmf((char)e.Key, 250);
			if (_mainViewModel.ActiveCallModel != null)
				_linphoneService.SendDtmf(_mainViewModel.ActiveCallModel.ActiveCall, (char)e.Key);
		}

		private void OnDialpadClicked(object sender, KeyPadEventArgs e)
		{
			_linphoneService.PlayDtmf((char)e.Key, 250);
		}

		private void OnSettingsChangeRequired(Enums.VATRPSettings settingsType)
		{
            if ((_settingsWindow != null) && _settingsWindow.IsVisible)
                return;
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(ctrlCall, OnAccountChangeRequested);
            }

			_mainViewModel.SettingsModel.SetActiveSettings(settingsType);
            _settingsWindow.Show();

            _settingsView.Show();
			_settingsView.Activate();
		}

		private void OnResetToDefaultConfiguration()
		{
			if (ServiceManager.Instance.ActiveCallPtr != IntPtr.Zero)
			{
				MessageBox.Show("There is an active call. Please try later");
				return;
			}

			if (defaultConfigRequest)
				return;
			defaultConfigRequest = true;

			if (RegistrationState == LinphoneRegistrationState.LinphoneRegistrationOk)
			{
				_linphoneService.Unregister(false);
			}
			else
			{
				ResetConfiguration();
			}
		}

	    private void OnStateChanged(object sender, EventArgs e)
	    {
	        Window_StateChanged(sender, e);

	        if (_mainViewModel.ActiveCallModel != null)
	        {
	            switch (WindowState)
	            {
	                case WindowState.Normal:
	                    this.SizeToContent = SizeToContent.WidthAndHeight;
	                    UpdateLayout();
	                    _mainViewModel.IsCallPanelDocked = true;
	                    break;
	                case WindowState.Minimized:
	                    this.SizeToContent = SizeToContent.Manual;
	                    if (_mainViewModel.ActiveCallModel.ActiveCall != null)
	                    {
	                        if (_mainViewModel.ActiveCallModel.ActiveCall.CallState == VATRPCallState.InProgress)
	                            _flashWindowHelper.FlashWindow(this);
	                    }
	            break;
	                case WindowState.Maximized:
                        
	                    break;
	            }
	        }
	        else
	        {
	            if (WindowState == WindowState.Normal)
	            {
	                _mainViewModel.IsCallPanelDocked = true;
	                this.SizeToContent = SizeToContent.WidthAndHeight;
                    UpdateLayout();
                    _mainViewModel.IsCallPanelDocked = false;
	            }
	        }
	    }

	    private void OnLinphoneCoreStarted(object sender, EventArgs e)
	    {
	        ServiceManager.Instance.LinphoneService.OnCameraMuteEvent += OnCameraMuted;
	        if (App.CurrentAccount != null && !string.IsNullOrEmpty(App.CurrentAccount.VideoMailUri))
	            ServiceManager.Instance.LinphoneService.SubscribeForVideoMWI(App.CurrentAccount.VideoMailUri);
	    }
        
        private void OnLinphoneCoreStopped(object sender, EventArgs e)
        {
            ServiceManager.Instance.LinphoneService.OnCameraMuteEvent -= OnCameraMuted;
            //ServiceManager.Instance.LinphoneService.Start(true);
        }

        private void OnCameraMuted(InfoEventBaseArgs args)
        {
            if (this.Dispatcher.Thread != Thread.CurrentThread)
            {
                this.Dispatcher.BeginInvoke((Action)(() => this.OnCameraMuted(args)));
                return;
            }

            var cameraMuteArgs = args as CameraMuteEventArgs;
            if (cameraMuteArgs != null)
            {
                if (_mainViewModel.ActiveCallModel.ActiveCall != null && 
                    (_mainViewModel.ActiveCallModel != null && 
                    _mainViewModel.ActiveCallModel.ActiveCall.NativeCallPtr == cameraMuteArgs.ActiveCall.NativeCallPtr))
                {
                    LOG.Info(string.Format("Remote side {0} camera", cameraMuteArgs.IsMuted ? "muted" : "unmuted"));
                    if (cameraMuteArgs.IsMuted)
                    {
                        ServiceManager.Instance.LinphoneService.SetVideoCallWindowHandle(IntPtr.Zero, true);
                        ctrlCall.ctrlVideo.DrawCameraImage = true;
                    }
                    else
                    {
                        ctrlCall.ctrlVideo.DrawCameraImage = false;
                        ctrlCall.AddVideoControl();
                    }
                }
            }
        }

	    private void OnDeclineMessageReceived(object sender, DeclineMessageArgs args)
	    {
	        var restartTimer = false;

	        lock (_mainViewModel.CallsViewModelList)
	        {
	            if (_mainViewModel.ActiveCallModel != null)
	            {
	                if (_mainViewModel.ActiveCallModel.Contact != null &&
	                    _mainViewModel.ActiveCallModel.Contact.Equals(args.Sender))
	                {
	                    _mainViewModel.ActiveCallModel.DeclinedMessage = args.DeclineMessage;
	                    _mainViewModel.ActiveCallModel.DeclinedMessageHeader = args.MessageHeader;
	                    switch (_mainViewModel.ActiveCallModel.CallState)
	                    {
	                        case VATRPCallState.Closed:
	                        case VATRPCallState.Declined:
                            _mainViewModel.ActiveCallModel.ShowInfoMessage = false;    
                            _mainViewModel.ActiveCallModel.ShowDeclinedMessage = true;
                            ctrlCall.ctrlOverlay.ShowInfoMsgWindow(false);
	                            restartTimer = true;
	                            break;
                            case VATRPCallState.Connected:
                            case VATRPCallState.StreamsRunning:
                                _mainViewModel.ActiveCallModel.ShowInfoMessage = true;
                                RearrangeUICallView(GetCallViewSize());
                                ctrlCall.ctrlOverlay.UpdateInfoMsg(args.MessageHeader, args.DeclineMessage);
                                ctrlCall.ctrlOverlay.ShowInfoMsgWindow(this.WindowState != WindowState.Minimized);
	                            _mainViewModel.ActiveCallModel.DeferredHideMessageControl();
	                            return;
	                    }
	                }
	            }
	            else
	            {
	                restartTimer = true;
	            }
	        }

	        if (!restartTimer)
	            return;
	        lock (deferredLock)
	        {
	            if (_mainViewModel.ActiveCallModel != null) 
                    _mainViewModel.ActiveCallModel.WaitForDeclineMessage = false;
	            deferredHideTimer.Stop();
                deferredHideTimer.Interval = TimeSpan.FromMilliseconds(DECLINE_WAIT_TIMEOUT);
	            deferredHideTimer.Start();
	        }
	    }

        private void OnLinphoneConnectivityChanged(bool reachable)
        {
            if (_isNeworkReachable == reachable)
                return;

            _isNeworkReachable = reachable;
            LOG.Info("### Network Connectivity changed to " + (reachable ? "UP" :"DOWN") );
            if (!_isNeworkReachable)
            {
                // TODO VATRP-3600, proceed to login 
                if (this.Dispatcher != null)
                {
                    if (this.Dispatcher.Thread != Thread.CurrentThread)
                    {
                        this.Dispatcher.BeginInvoke((Action) delegate
                        {
                            _mainViewModel.ContactModel.RegistrationState = LinphoneRegistrationState.LinphoneRegistrationFailed;
                            RegistrationFailReason = LinphoneReason.LinphoneReasonIOError;
                            if (_playRegistrationFailureNotify)
                            {
                                ServiceManager.Instance.SoundService.PlayConnectionChanged(false);
                                _playRegistrationFailureNotify = false;
                            }
                            _playRegisterNotify = true;
                            lock (regLock)
                            {
                                if (registrationTimer == null)
                                {
                                    registrationTimer = new System.Timers.Timer();
                                    registrationTimer.Elapsed +=
                                        new System.Timers.ElapsedEventHandler(RegistrationTimerTick);
                                }
                                else
                                {
                                    registrationTimer.Stop();
                                }
                                registrationTimer.Interval = 120000;
                                registrationTimer.Start();
                            }
                        });
                    }
                }
            }
            else
            {
                if (registrationTimer != null && registrationTimer.Enabled)
                {
                    DestroyRegistrationTimer();
                    signOutRequest = false;
                    if (_linphoneService != null) 
                        _linphoneService.Register();
                }
            }
        }

	    private void DestroyRegistrationTimer()
	    {
	        lock (regLock)
	        {
	            if (registrationTimer != null)
	            {
	                registrationTimer.Stop();
	                registrationTimer.Dispose();
	            }
	            registrationTimer = null;
	        }
	    }

        private void OnHideDeclineMessage(object sender, EventArgs e)
        {
            lock (deferredLock)
            {
                if (deferredHideTimer != null && deferredHideTimer.IsEnabled)
                {
                    deferredHideTimer.Stop();
                }
            }

            lock (_mainViewModel.CallsViewModelList)
            {
                if (_mainViewModel.ActiveCallModel != null)
                {
                    var viewModel = _mainViewModel.ActiveCallModel;
                    if (viewModel.CallState == VATRPCallState.Closed ||
                        viewModel.CallState == VATRPCallState.Declined ||
                        viewModel.CallState == VATRPCallState.Error)
                    {
                        viewModel.ShowInfoMessage = false;
                        viewModel.DeclinedMessage = string.Empty;
                        viewModel.ShowDeclinedMessage = false;

                        if (_mainViewModel.CallsViewModelList.Count == 1)
                        {
                            _mainViewModel.ActiveCallModel = null;
                            _mainViewModel.RemoveCalViewModel(viewModel);
                        }

                        if (ServiceManager.Instance.LinphoneService.GetActiveCallsCount == 0)
                            ctrlCall.SetCallViewModel(null);
                        _mainViewModel.IsCallPanelDocked = false;
                    }
                    else
                    {
                        if (viewModel.ShowInfoMessage)
                        {
                            _mainViewModel.ActiveCallModel.ShowInfoMessage = false;
                            ctrlCall.ctrlOverlay.ShowInfoMsgWindow(false);
                        }
                    }
                }
                else
                {
                    _mainViewModel.IsCallPanelDocked = false;
                }
            }
        }
	}
}
