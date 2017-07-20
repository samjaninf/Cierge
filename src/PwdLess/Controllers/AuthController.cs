﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using PwdLess.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PwdLess.Models;
using PwdLess.Filters;

namespace PwdLess.Controllers
{
    [Route("[controller]/[action]")]
    public class AuthController : Controller // TODO REMOVE CONTEXT AND MOVE ALL DB STUFF TO NEW REPOSITORY CLASS
    {
        private IAuthRepository _authRepo;
        private ISenderService _senderService;
        private ICallbackService _callbackService;
        private ILogger _logger;
        private AuthContext _context;

        public AuthController(IAuthRepository authRepo, 
            ISenderService senderService, 
            ICallbackService callbackService,
            ILogger<AuthController> logger,
            AuthContext context)
        {
            _authRepo = authRepo;
            _senderService = senderService;
            _callbackService = callbackService;
            _logger = logger;
            _context = context;
        }
        
        [HandleExceptions]
        public async Task<IActionResult> SendNonce(string contact, bool isAddingContact /*, string extraData = "email"*/)
        {
            if (_authRepo.DoesContactExist(contact)) // Returning user
                await _senderService.SendAsync(contact, await _authRepo.AddNonce(contact, UserState.ReturningUser), "ReturningUser");
            else if (isAddingContact) // Returning user adding contact
                await _senderService.SendAsync(contact, await _authRepo.AddNonce(contact, UserState.AddingContact), "AddingContact");
            else // New user
                await _senderService.SendAsync(contact, await _authRepo.AddNonce(contact, UserState.NewUser), "NewUser");
                
            return Ok();   
        }

        [HandleExceptions]
        public async Task<IActionResult> NonceToRefreshToken(string nonce, User user = null)
        {
            _authRepo.ValidateNonce(nonce);

            string userId;
            string contact = _authRepo.ContactOfNonce(nonce);

            if (_authRepo.GetNonceUserState(nonce) == UserState.NewUser)
            {
                if (!ModelState.IsValid) return BadRequest("You need to supply all additional user infomation.");

                user.UserId = userId = (string.Concat(Guid.NewGuid().ToString().Replace("-", "").Take(12))); /* ?? user.UserId */ // Users can't choose their own UserId, or TODO: they can but it can't be "admin"

                await _authRepo.AddUser(user); // TODO: batch all db CUD calls together
                await _authRepo.AddUserContact(userId, contact);
            }
            else {
                userId = _authRepo.UserIdOfContact(contact);
            }
            
            var refreshToken = await _authRepo.AddRefreshToken(userId);

            await _authRepo.DeleteNonce(contact);
        
            return Ok(refreshToken);
        }

        [Authorize, SetUserId, HandleExceptions]
        public async Task<IActionResult> NonceToAddContact(string nonce, string userId)
        {
            _authRepo.ValidateNonce(nonce);
            string contact = _authRepo.ContactOfNonce(nonce);
            await _authRepo.AddUserContact(userId, contact);
            await _authRepo.DeleteNonce(contact);
            return Ok();
        }

        [Authorize, SetUserId, HandleExceptions]
        public async Task<IActionResult> RemoveContact(string contact, string userId)
        {
            if (await _context.UserContacts.CountAsync(uc => uc.UserId == userId) <= 1)
                return BadRequest($"Sorry! Can't Remove last contact.");
            else
                _context.UserContacts.Remove(new UserContact() { Contact = contact, UserId = userId });
            return Ok();
        }

        [HandleExceptions]
        public IActionResult RefreshTokenToAccessToken(string refreshToken)
        {
            _authRepo.ValidateRefreshToken(refreshToken);
            string accessToken = _authRepo.RefreshTokenToAccessToken(refreshToken);
            
            return Ok(accessToken);
        }

        [Authorize, SetUserId, HandleExceptions]
        public async Task<IActionResult> RevokeRefreshToken(string userId)
        {
            await _authRepo.RevokeRefreshToken(userId);
            return Ok();
        }



        [Authorize, HandleExceptions]
        /// Validates tokens sent via authorization header
        /// Eg. Authorization: Bearer [token]
        ///     client_id    : defaultClient
        public IActionResult ValidateToken()
        {
            // Convert claims to JSON
            var sb = new StringBuilder();
            sb.Append("{"); // add opening parens
            foreach (var claim in HttpContext.User.Claims)
            {
                // add "key : value,"
                sb.Append($"\n\t\"{claim.Type.ToString()}\" : \"{claim.Value.ToString()}\",");
            }
            sb.Length--; // remove last comma
            sb.Append("\n}"); // add closing parens
            sb.Replace("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", "sub");
            var claimsJson = sb.ToString();


            return Ok(claimsJson);
        }
    }
}
