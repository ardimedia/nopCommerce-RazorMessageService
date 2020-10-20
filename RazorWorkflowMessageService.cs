﻿using System;
using System.Collections.Generic;
using System.Linq;
using Nop.Core;
using Nop.Core.Domain.Blogs;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Forums;
using Nop.Core.Domain.Messages;
using Nop.Core.Domain.News;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Shipping;
using Nop.Core.Domain.Stores;
using Nop.Core.Domain.Vendors;
using Nop.Services.Customers;
using Nop.Services.Events;
using Nop.Services.Localization;
using Nop.Services.Stores;
using Nop.Services.Messages;

namespace ToSic.Nop.Plugins.RazorMessageService
{
    /// <summary>
    /// This is a clone of Nop.Services.Messages.WorkflowMessageService
    /// </summary>
    /// <remarks>
    /// Extended SendNotification() with Parameter razorModel, adapted all Usages.
    /// </remarks>
    public class RazorWorkflowMessageService : IWorkflowMessageService
    {
        #region Fields

        private readonly IMessageTemplateService _messageTemplateService;
        private readonly IQueuedEmailService _queuedEmailService;
        private readonly ILanguageService _languageService;
        private readonly ITokenizer _tokenizer;
        private readonly IEmailAccountService _emailAccountService;
        private readonly IMessageTokenProvider _messageTokenProvider;
        private readonly IStoreService _storeService;
        private readonly IStoreContext _storeContext;
        private readonly EmailAccountSettings _emailAccountSettings;
        private readonly IEventPublisher _eventPublisher;

        #endregion

        #region Ctor

        public RazorWorkflowMessageService(IMessageTemplateService messageTemplateService,
            IQueuedEmailService queuedEmailService,
            ILanguageService languageService,
            ITokenizer tokenizer,
            IEmailAccountService emailAccountService,
            IMessageTokenProvider messageTokenProvider,
            IStoreService storeService,
            IStoreContext storeContext,
            EmailAccountSettings emailAccountSettings,
            IEventPublisher eventPublisher)
        {
            this._messageTemplateService = messageTemplateService;
            this._queuedEmailService = queuedEmailService;
            this._languageService = languageService;
            this._tokenizer = tokenizer;
            this._emailAccountService = emailAccountService;
            this._messageTokenProvider = messageTokenProvider;
            this._storeService = storeService;
            this._storeContext = storeContext;
            this._emailAccountSettings = emailAccountSettings;
            this._eventPublisher = eventPublisher;
        }

        #endregion

        #region Utilities

        protected virtual int SendNotification(MessageTemplate messageTemplate,
            EmailAccount emailAccount, int languageId, IEnumerable<Token> tokens,
            object razorModel,  // customized
            string toEmailAddress, string toName,
            string attachmentFilePath = null, string attachmentFileName = null,
            string replyToEmailAddress = null, string replyToName = null)
        {
            if (messageTemplate == null)
                throw new ArgumentNullException("messageTemplate");
            if (emailAccount == null)
                throw new ArgumentNullException("emailAccount");

            //retrieve localized message template data
            var bcc = messageTemplate.GetLocalized(mt => mt.BccEmailAddresses, languageId);
            var subject = messageTemplate.GetLocalized(mt => mt.Subject, languageId);
            var body = messageTemplate.GetLocalized(mt => mt.Body, languageId);

            #region Customized
            // Run Razor before tokenizer to prevent unneeded recompilation of the Razor-Template which would be very resource intensive

            // Razor-Parse Subject
            bool subjectSuccess;
            var subjectRazorParsed = Services.RazorTemplateParser.ParseSafe(messageTemplate.Id, subject, razorModel, out subjectSuccess);
            if (subjectSuccess)
                subject = subjectRazorParsed;
            else
                subject += subjectRazorParsed; // in case of an error, append the error-text returned

            // Razor-Parse Body
            bool bodySuccess;
            var bodyRazorParsed = Services.RazorTemplateParser.ParseSafe(messageTemplate.Id, body, razorModel, out bodySuccess);
            if (bodySuccess)
                body = bodyRazorParsed;
            else
                body += bodyRazorParsed; // in case of an error, append the error-text returned
            #endregion

            //Replace subject and body tokens
            var subjectReplaced = this._tokenizer.Replace(subject, tokens, false);
            var bodyReplaced = this._tokenizer.Replace(body, tokens, true);

            var email = new QueuedEmail
            {
                Priority = QueuedEmailPriority.High,
                From = emailAccount.Email,
                FromName = emailAccount.DisplayName,
                To = toEmailAddress,
                ToName = toName,
                ReplyTo = replyToEmailAddress,
                ReplyToName = replyToName,
                CC = string.Empty,
                Bcc = bcc,
                Subject = subjectReplaced,
                Body = bodyReplaced,
                AttachmentFilePath = attachmentFilePath,
                AttachmentFileName = attachmentFileName,
                AttachedDownloadId = messageTemplate.AttachedDownloadId,
                CreatedOnUtc = DateTime.UtcNow,
                EmailAccountId = emailAccount.Id
#if NOP380
				,DontSendBeforeDateUtc = !messageTemplate..DelayBeforeSend.HasValue ? null
				: (DateTime?)(DateTime.UtcNow + TimeSpan.FromHours(messageTemplate.DelayPeriod.ToHours(messageTemplate.DelayBeforeSend.Value)))
#endif
            };

            this._queuedEmailService.InsertQueuedEmail(email);
            return email.Id;
        }

        protected virtual MessageTemplate GetActiveMessageTemplate(string messageTemplateName, int storeId)
        {
            var messageTemplate = this._messageTemplateService.GetMessageTemplateByName(messageTemplateName, storeId);

            //no template found
            if (messageTemplate == null)
                return null;

            //ensure it's active
            var isActive = messageTemplate.IsActive;
            if (!isActive)
                return null;

            return messageTemplate;
        }

        protected virtual EmailAccount GetEmailAccountOfMessageTemplate(MessageTemplate messageTemplate, int languageId)
        {
            var emailAccountId = messageTemplate.GetLocalized(mt => mt.EmailAccountId, languageId);
            //some 0 validation (for localizable "Email account" dropdownlist which saves 0 if "Standard" value is chosen)
            if (emailAccountId == 0)
                emailAccountId = messageTemplate.EmailAccountId;

            var emailAccount = this._emailAccountService.GetEmailAccountById(emailAccountId);
            if (emailAccount == null)
                emailAccount = this._emailAccountService.GetEmailAccountById(this._emailAccountSettings.DefaultEmailAccountId);
            if (emailAccount == null)
                emailAccount = this._emailAccountService.GetAllEmailAccounts().FirstOrDefault();
            return emailAccount;

        }

        protected virtual int EnsureLanguageIsActive(int languageId, int storeId)
        {
            //load language by specified ID
            var language = this._languageService.GetLanguageById(languageId);

            if (language == null || !language.Published)
            {
                //load any language from the specified store
                language = this._languageService.GetAllLanguages(storeId: storeId).FirstOrDefault();
            }
            if (language == null || !language.Published)
            {
                //load any language
                language = this._languageService.GetAllLanguages().FirstOrDefault();
            }

            if (language == null)
                throw new Exception("No active language could be loaded");
            return language.Id;
        }

        #endregion

        #region Methods

        #region Customer workflow

        /// <summary>
        /// Sends 'New customer' notification message to a store owner
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendCustomerRegisteredNotificationMessage(Customer customer, int languageId)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("NewCustomer.Notification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer }, // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a welcome message to a customer
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendCustomerWelcomeMessage(Customer customer, int languageId)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Customer.WelcomeMessage", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = customer.Email;
            var toName = customer.GetFullName();
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer }, // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an email validation message to a customer
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendCustomerEmailValidationMessage(Customer customer, int languageId)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Customer.EmailValidationMessage", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);


            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = customer.Email;
            var toName = customer.GetFullName();
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer }, // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends password recovery message to a customer
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendCustomerPasswordRecoveryMessage(Customer customer, int languageId)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Customer.PasswordRecovery", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);


            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = customer.Email;
            var toName = customer.GetFullName();
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer }, // customized
                toEmail, toName);
        }

        #endregion

        #region Order workflow

        /// <summary>
        /// Sends an order placed notification to a vendor
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="vendor">Vendor instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderPlacedVendorNotification(Order order, Vendor vendor, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            if (vendor == null)
                throw new ArgumentNullException("vendor");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderPlaced.VendorNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId, vendor.Id);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = vendor.Email;
            var toName = vendor.Name;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer, Vendor = vendor },  // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order placed notification to a store owner
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderPlacedStoreOwnerNotification(Order order, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderPlaced.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order paid notification to a store owner
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderPaidStoreOwnerNotification(Order order, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderPaid.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order paid notification to a customer
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <param name="attachmentFilePath">Attachment file path</param>
        /// <param name="attachmentFileName">Attachment file name. If specified, then this file name will be sent to a recipient. Otherwise, "AttachmentFilePath" name will be used.</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderPaidCustomerNotification(Order order, int languageId,
            string attachmentFilePath = null, string attachmentFileName = null)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderPaid.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName,
                attachmentFilePath,
                attachmentFileName);
        }

        /// <summary>
        /// Sends an order paid notification to a vendor
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="vendor">Vendor instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderPaidVendorNotification(Order order, Vendor vendor, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            if (vendor == null)
                throw new ArgumentNullException("vendor");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderPaid.VendorNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId, vendor.Id);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = vendor.Email;
            var toName = vendor.Name;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order placed notification to a customer
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <param name="attachmentFilePath">Attachment file path</param>
        /// <param name="attachmentFileName">Attachment file name. If specified, then this file name will be sent to a recipient. Otherwise, "AttachmentFilePath" name will be used.</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderPlacedCustomerNotification(Order order, int languageId,
            string attachmentFilePath = null, string attachmentFileName = null)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderPlaced.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName,
                attachmentFilePath,
                attachmentFileName);
        }

        /// <summary>
        /// Sends a shipment sent notification to a customer
        /// </summary>
        /// <param name="shipment">Shipment</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendShipmentSentCustomerNotification(Shipment shipment, int languageId)
        {
            if (shipment == null)
                throw new ArgumentNullException("shipment");

            var order = shipment.Order;
            if (order == null)
                throw new Exception("Order cannot be loaded");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("ShipmentSent.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddShipmentTokens(tokens, shipment, languageId);
            this._messageTokenProvider.AddOrderTokens(tokens, shipment.Order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, shipment.Order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Shipment = shipment, shipment.Order, shipment.Order.Customer },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a shipment delivered notification to a customer
        /// </summary>
        /// <param name="shipment">Shipment</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendShipmentDeliveredCustomerNotification(Shipment shipment, int languageId)
        {
            if (shipment == null)
                throw new ArgumentNullException("shipment");

            var order = shipment.Order;
            if (order == null)
                throw new Exception("Order cannot be loaded");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("ShipmentDelivered.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddShipmentTokens(tokens, shipment, languageId);
            this._messageTokenProvider.AddOrderTokens(tokens, shipment.Order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, shipment.Order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Shipment = shipment, shipment.Order, shipment.Order.Customer },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order completed notification to a customer
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <param name="attachmentFilePath">Attachment file path</param>
        /// <param name="attachmentFileName">Attachment file name. If specified, then this file name will be sent to a recipient. Otherwise, "AttachmentFilePath" name will be used.</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderCompletedCustomerNotification(Order order, int languageId,
            string attachmentFilePath = null, string attachmentFileName = null)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderCompleted.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName,
                attachmentFilePath,
                attachmentFileName);
        }

        /// <summary>
        /// Sends an order cancelled notification to a customer
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderCancelledCustomerNotification(Order order, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderCancelled.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order refunded notification to a store owner
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="refundedAmount">Amount refunded</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderRefundedStoreOwnerNotification(Order order, decimal refundedAmount, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderRefunded.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddOrderRefundedTokens(tokens, order, refundedAmount);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer, RefundedAmount = refundedAmount },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends an order refunded notification to a customer
        /// </summary>
        /// <param name="order">Order instance</param>
        /// <param name="refundedAmount">Amount refunded</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendOrderRefundedCustomerNotification(Order order, decimal refundedAmount, int languageId)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("OrderRefunded.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, order, languageId);
            this._messageTokenProvider.AddOrderRefundedTokens(tokens, order, refundedAmount);
            this._messageTokenProvider.AddCustomerTokens(tokens, order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = order, order.Customer, refundedAmount },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a new order note added notification to a customer
        /// </summary>
        /// <param name="orderNote">Order note</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewOrderNoteAddedCustomerNotification(OrderNote orderNote, int languageId)
        {
            if (orderNote == null)
                throw new ArgumentNullException("orderNote");

            var order = orderNote.Order;

            var store = this._storeService.GetStoreById(order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Customer.NewOrderNote", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderNoteTokens(tokens, orderNote);
            this._messageTokenProvider.AddOrderTokens(tokens, orderNote.Order, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, orderNote.Order.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = order.BillingAddress.Email;
            var toName = string.Format("{0} {1}", order.BillingAddress.FirstName, order.BillingAddress.LastName);
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, OrderNote = orderNote, orderNote.Order, orderNote.Order.Customer },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a "Recurring payment cancelled" notification to a store owner
        /// </summary>
        /// <param name="recurringPayment">Recurring payment</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendRecurringPaymentCancelledStoreOwnerNotification(RecurringPayment recurringPayment, int languageId)
        {
            if (recurringPayment == null)
                throw new ArgumentNullException("recurringPayment");

            var store = this._storeService.GetStoreById(recurringPayment.InitialOrder.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("RecurringPaymentCancelled.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddOrderTokens(tokens, recurringPayment.InitialOrder, languageId);
            this._messageTokenProvider.AddCustomerTokens(tokens, recurringPayment.InitialOrder.Customer);
            this._messageTokenProvider.AddRecurringPaymentTokens(tokens, recurringPayment);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Order = recurringPayment.InitialOrder, recurringPayment.InitialOrder.Customer, RecurringPayment = recurringPayment },  // customized
                toEmail, toName);
        }

        #endregion

        #region Newsletter workflow

        /// <summary>
        /// Sends a newsletter subscription activation message
        /// </summary>
        /// <param name="subscription">Newsletter subscription</param>
        /// <param name="languageId">Language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewsLetterSubscriptionActivationMessage(NewsLetterSubscription subscription,
            int languageId)
        {
            if (subscription == null)
                throw new ArgumentNullException("subscription");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("NewsLetterSubscription.ActivationMessage", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddNewsLetterSubscriptionTokens(tokens, subscription);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = subscription.Email;
            var toName = "";
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Subscription = subscription }, // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a newsletter subscription deactivation message
        /// </summary>
        /// <param name="subscription">Newsletter subscription</param>
        /// <param name="languageId">Language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewsLetterSubscriptionDeactivationMessage(NewsLetterSubscription subscription,
            int languageId)
        {
            if (subscription == null)
                throw new ArgumentNullException("subscription");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("NewsLetterSubscription.DeactivationMessage", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddNewsLetterSubscriptionTokens(tokens, subscription);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = subscription.Email;
            var toName = "";
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Subscription = subscription },   // customized
                toEmail, toName);
        }

        #endregion

        #region Send a message to a friend

        /// <summary>
        /// Sends "email a friend" message
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="languageId">Message language identifier</param>
        /// <param name="product">Product instance</param>
        /// <param name="customerEmail">Customer's email</param>
        /// <param name="friendsEmail">Friend's email</param>
        /// <param name="personalMessage">Personal message</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendProductEmailAFriendMessage(Customer customer, int languageId,
            Product product, string customerEmail, string friendsEmail, string personalMessage)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            if (product == null)
                throw new ArgumentNullException("product");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Service.EmailAFriend", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);
            this._messageTokenProvider.AddProductTokens(tokens, product, languageId);
            tokens.Add(new Token("EmailAFriend.PersonalMessage", personalMessage, true));
            tokens.Add(new Token("EmailAFriend.Email", customerEmail));

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = friendsEmail;
            var toName = "";
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer, Product = product, PersonalMessage = personalMessage, CustomerEmail = customerEmail },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends wishlist "email a friend" message
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="languageId">Message language identifier</param>
        /// <param name="customerEmail">Customer's email</param>
        /// <param name="friendsEmail">Friend's email</param>
        /// <param name="personalMessage">Personal message</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendWishlistEmailAFriendMessage(Customer customer, int languageId,
             string customerEmail, string friendsEmail, string personalMessage)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Wishlist.EmailAFriend", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);
            tokens.Add(new Token("Wishlist.PersonalMessage", personalMessage, true));
            tokens.Add(new Token("Wishlist.Email", customerEmail));

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = friendsEmail;
            var toName = "";
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer, PersonalMessage = personalMessage, CustomerEmail = customerEmail },   // customized
                toEmail, toName);
        }

        #endregion

        #region Return requests

        /// <summary>
        /// Sends 'New Return Request' message to a store owner
        /// </summary>
        /// <param name="returnRequest">Return request</param>
        /// <param name="orderItem">Order item</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewReturnRequestStoreOwnerNotification(ReturnRequest returnRequest, OrderItem orderItem, int languageId)
        {
            if (returnRequest == null)
                throw new ArgumentNullException("returnRequest");

            var store = this._storeService.GetStoreById(orderItem.Order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("NewReturnRequest.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, returnRequest.Customer);
            this._messageTokenProvider.AddReturnRequestTokens(tokens, returnRequest, orderItem);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, returnRequest.Customer, ReturnRequest = returnRequest, OrderItem = orderItem },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends 'Return Request status changed' message to a customer
        /// </summary>
        /// <param name="returnRequest">Return request</param>
        /// <param name="orderItem">Order item</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendReturnRequestStatusChangedCustomerNotification(ReturnRequest returnRequest, OrderItem orderItem, int languageId)
        {
            if (returnRequest == null)
                throw new ArgumentNullException("returnRequest");

            var store = this._storeService.GetStoreById(orderItem.Order.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("ReturnRequestStatusChanged.CustomerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, returnRequest.Customer);
            this._messageTokenProvider.AddReturnRequestTokens(tokens, returnRequest, orderItem);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            string toEmail = returnRequest.Customer.IsGuest() ?
                orderItem.Order.BillingAddress.Email :
                returnRequest.Customer.Email;
            var toName = returnRequest.Customer.IsGuest() ?
                orderItem.Order.BillingAddress.FirstName :
                returnRequest.Customer.GetFullName();
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, returnRequest.Customer, ReturnRequest = returnRequest, OrderItem = orderItem },    // customized
                toEmail, toName);
        }

        #endregion

        #region Forum Notifications

        /// <summary>
        /// Sends a forum subscription message to a customer
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="forumTopic">Forum Topic</param>
        /// <param name="forum">Forum</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public int SendNewForumTopicMessage(Customer customer,
            ForumTopic forumTopic, Forum forum, int languageId)
        {
            if (customer == null)
            {
                throw new ArgumentNullException("customer");
            }
            var store = this._storeContext.CurrentStore;

            var messageTemplate = this.GetActiveMessageTemplate("Forums.NewForumTopic", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddForumTopicTokens(tokens, forumTopic);
            this._messageTokenProvider.AddForumTokens(tokens, forumTopic.Forum);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = customer.Email;
            var toName = customer.GetFullName();

            return this.SendNotification(messageTemplate, emailAccount, languageId, tokens,
                new { Store = store, ForumTopic = forumTopic, forumTopic.Forum },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a forum subscription message to a customer
        /// </summary>
        /// <param name="customer">Customer instance</param>
        /// <param name="forumPost">Forum post</param>
        /// <param name="forumTopic">Forum Topic</param>
        /// <param name="forum">Forum</param>
        /// <param name="friendlyForumTopicPageIndex">Friendly (starts with 1) forum topic page to use for URL generation</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public int SendNewForumPostMessage(Customer customer,
            ForumPost forumPost, ForumTopic forumTopic,
            Forum forum, int friendlyForumTopicPageIndex, int languageId)
        {
            if (customer == null)
            {
                throw new ArgumentNullException("customer");
            }

            var store = this._storeContext.CurrentStore;

            var messageTemplate = this.GetActiveMessageTemplate("Forums.NewForumPost", store.Id);
            if (messageTemplate == null)
            {
                return 0;
            }

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddForumPostTokens(tokens, forumPost);
            this._messageTokenProvider.AddForumTopicTokens(tokens, forumPost.ForumTopic,
                friendlyForumTopicPageIndex, forumPost.Id);
            this._messageTokenProvider.AddForumTokens(tokens, forumPost.ForumTopic.Forum);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = customer.Email;
            var toName = customer.GetFullName();

            return this.SendNotification(messageTemplate, emailAccount, languageId, tokens,
                new { Store = store, Customer = customer, ForumPost = forumPost, forumPost.ForumTopic, forumPost.ForumTopic.Forum },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a private message notification
        /// </summary>
        /// <param name="privateMessage">Private message</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public int SendPrivateMessageNotification(PrivateMessage privateMessage, int languageId)
        {
            if (privateMessage == null)
            {
                throw new ArgumentNullException("privateMessage");
            }

            var store = this._storeService.GetStoreById(privateMessage.StoreId) ?? this._storeContext.CurrentStore;

            var messageTemplate = this.GetActiveMessageTemplate("Customer.NewPM", store.Id);
            if (messageTemplate == null)
            {
                return 0;
            }

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddPrivateMessageTokens(tokens, privateMessage);
            this._messageTokenProvider.AddCustomerTokens(tokens, privateMessage.ToCustomer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = privateMessage.ToCustomer.Email;
            var toName = privateMessage.ToCustomer.GetFullName();

            return this.SendNotification(messageTemplate, emailAccount, languageId, tokens,
                new { Store = store, PrivateMessage = privateMessage }, // customized
                toEmail, toName);
        }

        #endregion

        #region Misc

        /// <summary>
        /// Sends 'New vendor account submitted' message to a store owner
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="vendor">Vendor</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewVendorAccountApplyStoreOwnerNotification(Customer customer, Vendor vendor, int languageId)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            if (vendor == null)
                throw new ArgumentNullException("vendor");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("VendorAccountApply.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);
            this._messageTokenProvider.AddVendorTokens(tokens, vendor);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer, Vendor = vendor },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends 'Vendor information changed' message to a store owner
        /// </summary>
        /// <param name="vendor">Vendor</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendVendorInformationChangeNotification(Vendor vendor, int languageId)
        {
            if (vendor == null)
                throw new ArgumentNullException("vendor");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("VendorInformationChange.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddVendorTokens(tokens, vendor);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Vendor = vendor },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a gift card notification
        /// </summary>
        /// <param name="giftCard">Gift card</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendGiftCardNotification(GiftCard giftCard, int languageId)
        {
            if (giftCard == null)
                throw new ArgumentNullException("giftCard");

            Store store = null;
            var order = giftCard.PurchasedWithOrderItem != null ?
                giftCard.PurchasedWithOrderItem.Order :
                null;
            if (order != null)
                store = this._storeService.GetStoreById(order.StoreId);
            if (store == null)
                store = this._storeContext.CurrentStore;

            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("GiftCard.Notification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddGiftCardTokens(tokens, giftCard);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);
            var toEmail = giftCard.RecipientEmail;
            var toName = giftCard.RecipientName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, GiftCard = giftCard }, // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a product review notification message to a store owner
        /// </summary>
        /// <param name="productReview">Product review</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendProductReviewNotificationMessage(ProductReview productReview,
            int languageId)
        {
            if (productReview == null)
                throw new ArgumentNullException("productReview");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Product.ProductReview", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddProductReviewTokens(tokens, productReview);
            this._messageTokenProvider.AddCustomerTokens(tokens, productReview.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, ProductReview = productReview },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a "quantity below" notification to a store owner
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendQuantityBelowStoreOwnerNotification(Product product, int languageId)
        {
            if (product == null)
                throw new ArgumentNullException("product");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("QuantityBelow.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddProductTokens(tokens, product, languageId);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Product = product },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a "quantity below" notification to a store owner
        /// </summary>
        /// <param name="combination">Attribute combination</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendQuantityBelowStoreOwnerNotification(ProductAttributeCombination combination, int languageId)
        {
            if (combination == null)
                throw new ArgumentNullException("combination");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("QuantityBelow.AttributeCombination.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            var product = combination.Product;

            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddProductTokens(tokens, product, languageId);
            this._messageTokenProvider.AddAttributeCombinationTokens(tokens, combination, languageId);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Product = product, Combination = combination },    // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a "new VAT submitted" notification to a store owner
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="vatName">Received VAT name</param>
        /// <param name="vatAddress">Received VAT address</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewVatSubmittedStoreOwnerNotification(Customer customer,
            string vatName, string vatAddress, int languageId)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("NewVATSubmitted.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, customer);
            tokens.Add(new Token("VatValidationResult.Name", vatName));
            tokens.Add(new Token("VatValidationResult.Address", vatAddress));

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, Customer = customer, VatName = vatName, VatAddress = vatAddress }, // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a blog comment notification message to a store owner
        /// </summary>
        /// <param name="blogComment">Blog comment</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendBlogCommentNotificationMessage(BlogComment blogComment, int languageId)
        {
            if (blogComment == null)
                throw new ArgumentNullException("blogComment");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Blog.BlogComment", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddBlogCommentTokens(tokens, blogComment);
            this._messageTokenProvider.AddCustomerTokens(tokens, blogComment.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, BlogComment = blogComment },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a news comment notification message to a store owner
        /// </summary>
        /// <param name="newsComment">News comment</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendNewsCommentNotificationMessage(NewsComment newsComment, int languageId)
        {
            if (newsComment == null)
                throw new ArgumentNullException("newsComment");

            var store = this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("News.NewsComment", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddNewsCommentTokens(tokens, newsComment);
            this._messageTokenProvider.AddCustomerTokens(tokens, newsComment.Customer);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var toEmail = emailAccount.Email;
            var toName = emailAccount.DisplayName;
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, NewsComment = newsComment },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a 'Back in stock' notification message to a customer
        /// </summary>
        /// <param name="subscription">Subscription</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendBackInStockNotification(BackInStockSubscription subscription, int languageId)
        {
            if (subscription == null)
                throw new ArgumentNullException("subscription");

            var store = this._storeService.GetStoreById(subscription.StoreId) ?? this._storeContext.CurrentStore;
            languageId = this.EnsureLanguageIsActive(languageId, store.Id);

            var messageTemplate = this.GetActiveMessageTemplate("Customer.BackInStock", store.Id);
            if (messageTemplate == null)
                return 0;

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //tokens
            var tokens = new List<Token>();
            this._messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            this._messageTokenProvider.AddCustomerTokens(tokens, subscription.Customer);
            this._messageTokenProvider.AddBackInStockTokens(tokens, subscription);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            var customer = subscription.Customer;
            var toEmail = customer.Email;
            var toName = customer.GetFullName();
            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new { Store = store, subscription.Customer, BackInStockSubscription = subscription },   // customized
                toEmail, toName);
        }

        /// <summary>
        /// Sends a test email
        /// </summary>
        /// <param name="messageTemplateId">Message template identifier</param>
        /// <param name="sendToEmail">Send to email</param>
        /// <param name="tokens">Tokens</param>
        /// <param name="languageId">Message language identifier</param>
        /// <returns>Queued email identifier</returns>
        public virtual int SendTestEmail(int messageTemplateId, string sendToEmail,
            List<Token> tokens, int languageId)
        {
            var messageTemplate = this._messageTemplateService.GetMessageTemplateById(messageTemplateId);
            if (messageTemplate == null)
                throw new ArgumentException("Template cannot be loaded");

            //email account
            var emailAccount = this.GetEmailAccountOfMessageTemplate(messageTemplate, languageId);

            //event notification
            this._eventPublisher.MessageTokensAdded(messageTemplate, tokens);

            return this.SendNotification(messageTemplate, emailAccount,
                languageId, tokens,
                new DummyMessageModel(), // customized
                sendToEmail, null);
        }

        #endregion

        #endregion
    }
}
