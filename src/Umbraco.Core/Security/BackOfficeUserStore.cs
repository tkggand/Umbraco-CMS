using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Security;
using AutoMapper;
using Microsoft.AspNet.Identity;
using Umbraco.Core.Models.Identity;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Services;

namespace Umbraco.Core.Security
{
    public class BackOfficeUserStore : DisposableObject, 
        IUserStore<BackOfficeIdentityUser, int>, 
        IUserPasswordStore<BackOfficeIdentityUser, int>, 
        IUserEmailStore<BackOfficeIdentityUser, int>, 
        IUserLoginStore<BackOfficeIdentityUser, int>,
        IUserRoleStore<BackOfficeIdentityUser, int>

        //TODO: This will require additional columns/tables
        //IUserLockoutStore<BackOfficeIdentityUser, int>

        //TODO: Implement this - might need to add a new column for this 
        // http://stackoverflow.com/questions/19487322/what-is-asp-net-identitys-iusersecuritystampstoretuser-interface
        //IUserSecurityStampStore<BackOfficeIdentityUser, int>

        //TODO: To do this we need to implement IQueryable - seems pretty overkill?
        //IQueryableUserStore<BackOfficeIdentityUser, int>
    {
        private readonly IUserService _userService;
        private readonly IExternalLoginService _externalLoginService;

        public BackOfficeUserStore(IUserService userService, IExternalLoginService externalLoginService, MembershipProviderBase usersMembershipProvider)
        {
            _userService = userService;
            _externalLoginService = externalLoginService;
            if (userService == null) throw new ArgumentNullException("userService");
            if (usersMembershipProvider == null) throw new ArgumentNullException("usersMembershipProvider");
            if (externalLoginService == null) throw new ArgumentNullException("externalLoginService");

            _userService = userService;
            _externalLoginService = externalLoginService;

            if (usersMembershipProvider.PasswordFormat != MembershipPasswordFormat.Hashed)
            {
                throw new InvalidOperationException("Cannot use ASP.Net Identity with UmbracoMembersUserStore when the password format is not Hashed");
            }
        }

        /// <summary>
        /// Handles the disposal of resources. Derived from abstract class <see cref="DisposableObject"/> which handles common required locking logic.
        /// </summary>
        protected override void DisposeResources()
        {
        }

        /// <summary>
        /// Insert a new user
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task CreateAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            var userType = _userService.GetUserTypeByAlias(
                user.UserTypeAlias.IsNullOrWhiteSpace() ? _userService.GetDefaultMemberType() : user.UserTypeAlias);

            var member = new User(userType)
            {
                DefaultToLiveEditing = false,
                Email = user.Email,
                Language = Configuration.GlobalSettings.DefaultUILanguage,
                Name = user.Name,
                Username = user.UserName,
                StartContentId = -1,
                StartMediaId = -1,
                IsLockedOut = false,
                IsApproved = true
            };

            UpdateMemberProperties(member, user);

            //the password must be 'something' it could be empty if authenticating
            // with an external provider so we'll just generate one and prefix it, the 
            // prefix will help us determine if the password hasn't actually been specified yet.
            if (member.RawPasswordValue.IsNullOrWhiteSpace())
            {
                //this will hash the guid with a salt so should be nicely random
                var aspHasher = new PasswordHasher();
                member.RawPasswordValue = "___UIDEMPTYPWORD__" +
                    aspHasher.HashPassword(Guid.NewGuid().ToString("N"));

            }
            _userService.Save(member);

            //re-assign id
            user.Id = member.Id;

            return Task.FromResult(0);
        }

        /// <summary>
        /// Update a user
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public async Task UpdateAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            var asInt = user.Id.TryConvertTo<int>();
            if (asInt == false)
            {
                throw new InvalidOperationException("The user id must be an integer to work with the Umbraco");
            }

            var found = _userService.GetUserById(asInt.Result);
            if (found != null)
            {
                if (UpdateMemberProperties(found, user))
                {
                    _userService.Save(found);
                }

                if (user.LoginsChanged)
                {
                    var logins = await GetLoginsAsync(user);
                    _externalLoginService.SaveUserLogins(found.Id, logins);
                }
            }           
        }

        /// <summary>
        /// Delete a user
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task DeleteAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            var asInt = user.Id.TryConvertTo<int>();
            if (asInt == false)
            {
                throw new InvalidOperationException("The user id must be an integer to work with the Umbraco");
            }

            var found = _userService.GetUserById(asInt.Result);
            if (found != null)
            {
                _userService.Delete(found);
            }
            _externalLoginService.DeleteUserLogins(asInt.Result);

            return Task.FromResult(0);
        }

        /// <summary>
        /// Finds a user
        /// </summary>
        /// <param name="userId"/>
        /// <returns/>
        public Task<BackOfficeIdentityUser> FindByIdAsync(int userId)
        {
            var user = _userService.GetUserById(userId);
            if (user == null)
            {
                return null;
            }
            return Task.FromResult(AssignLoginsCallback(Mapper.Map<BackOfficeIdentityUser>(user)));
        }

        /// <summary>
        /// Find a user by name
        /// </summary>
        /// <param name="userName"/>
        /// <returns/>
        public Task<BackOfficeIdentityUser> FindByNameAsync(string userName)
        {
            var user = _userService.GetByUsername(userName);
            if (user == null)
            {
                return null;
            }

            var result = AssignLoginsCallback(Mapper.Map<BackOfficeIdentityUser>(user));

            return Task.FromResult(result);
        }

        /// <summary>
        /// Set the user password hash
        /// </summary>
        /// <param name="user"/><param name="passwordHash"/>
        /// <returns/>
        public Task SetPasswordHashAsync(BackOfficeIdentityUser user, string passwordHash)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (passwordHash.IsNullOrWhiteSpace()) throw new ArgumentNullException("passwordHash");

            user.PasswordHash = passwordHash;

            return Task.FromResult(0);
        }

        /// <summary>
        /// Get the user password hash
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task<string> GetPasswordHashAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.PasswordHash);
        }

        /// <summary>
        /// Returns true if a user has a password set
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task<bool> HasPasswordAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.PasswordHash.IsNullOrWhiteSpace() == false);
        }

        /// <summary>
        /// Set the user email
        /// </summary>
        /// <param name="user"/><param name="email"/>
        /// <returns/>
        public Task SetEmailAsync(BackOfficeIdentityUser user, string email)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (email.IsNullOrWhiteSpace()) throw new ArgumentNullException("email");

            user.Email = email;

            return Task.FromResult(0);
        }

        /// <summary>
        /// Get the user email
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task<string> GetEmailAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.Email);
        }

        /// <summary>
        /// Returns true if the user email is confirmed
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task<bool> GetEmailConfirmedAsync(BackOfficeIdentityUser user)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Sets whether the user email is confirmed
        /// </summary>
        /// <param name="user"/><param name="confirmed"/>
        /// <returns/>
        public Task SetEmailConfirmedAsync(BackOfficeIdentityUser user, bool confirmed)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Returns the user associated with this email
        /// </summary>
        /// <param name="email"/>
        /// <returns/>
        public Task<BackOfficeIdentityUser> FindByEmailAsync(string email)
        {
            var user = _userService.GetByEmail(email);
            var result = user == null
                ? null
                : Mapper.Map<BackOfficeIdentityUser>(user);

            return Task.FromResult(AssignLoginsCallback(result));
        }

        /// <summary>
        /// Adds a user login with the specified provider and key
        /// </summary>
        /// <param name="user"/><param name="login"/>
        /// <returns/>
        public Task AddLoginAsync(BackOfficeIdentityUser user, UserLoginInfo login)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (login == null) throw new ArgumentNullException("login");

            var logins = user.Logins;
            var instance = new IdentityUserLogin(login.LoginProvider, login.ProviderKey, user.Id);
            var userLogin = instance;
            logins.Add(userLogin);

            return Task.FromResult(0);
        }

        /// <summary>
        /// Removes the user login with the specified combination if it exists
        /// </summary>
        /// <param name="user"/><param name="login"/>
        /// <returns/>
        public Task RemoveLoginAsync(BackOfficeIdentityUser user, UserLoginInfo login)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (login == null) throw new ArgumentNullException("login");

            var provider = login.LoginProvider;
            var key = login.ProviderKey;
            var userLogin = user.Logins.SingleOrDefault((l => l.LoginProvider == provider && l.ProviderKey == key));
            if (userLogin != null)
                user.Logins.Remove(userLogin);

            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns the linked accounts for this user
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task<IList<UserLoginInfo>> GetLoginsAsync(BackOfficeIdentityUser user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult((IList<UserLoginInfo>)
                user.Logins.Select(l => new UserLoginInfo(l.LoginProvider, l.ProviderKey)).ToList());
        }

        /// <summary>
        /// Returns the user associated with this login
        /// </summary>
        /// <returns/>
        public Task<BackOfficeIdentityUser> FindAsync(UserLoginInfo login)
        {
            //get all logins associated with the login id
            var result = _externalLoginService.Find(login).ToArray();
            if (result.Any())
            {
                //return the first member that matches the result
                var output = (from l in result
                            select _userService.GetUserById(l.UserId)
                                into user
                                where user != null
                                  select Mapper.Map<BackOfficeIdentityUser>(user)).FirstOrDefault();

                return Task.FromResult(AssignLoginsCallback(output));
            }

            return Task.FromResult<BackOfficeIdentityUser>(null);
        }

        private BackOfficeIdentityUser AssignLoginsCallback(BackOfficeIdentityUser user)
        {
            if (user != null)
            {
                user.SetLoginsCallback(new Lazy<IEnumerable<IIdentityUserLogin>>(() =>
                            _externalLoginService.GetAll(user.Id)));
            }
            return user;
        }

        private bool UpdateMemberProperties(Models.Membership.IUser user, BackOfficeIdentityUser identityUser)
        {
            var anythingChanged = false;
            //don't assign anything if nothing has changed as this will trigger
            //the track changes of the model
            if (user.Name != identityUser.Name && identityUser.Name.IsNullOrWhiteSpace() == false)
            {
                anythingChanged = true;
                user.Name = identityUser.Name;
            }
            if (user.Email != identityUser.Email && identityUser.Email.IsNullOrWhiteSpace() == false)
            {
                anythingChanged = true;
                user.Email = identityUser.Email;
            }
            if (user.FailedPasswordAttempts != identityUser.AccessFailedCount)
            {
                anythingChanged = true;
                user.FailedPasswordAttempts = identityUser.AccessFailedCount;
            }
            if (user.IsLockedOut != identityUser.LockoutEnabled)
            {
                anythingChanged = true;
                user.IsLockedOut = identityUser.LockoutEnabled;
            }
            if (user.Username != identityUser.UserName && identityUser.UserName.IsNullOrWhiteSpace() == false)
            {
                anythingChanged = true;
                user.Username = identityUser.UserName;
            }
            if (user.RawPasswordValue != identityUser.PasswordHash && identityUser.PasswordHash.IsNullOrWhiteSpace() == false)
            {
                anythingChanged = true;
                user.RawPasswordValue = identityUser.PasswordHash;
            }

            if (user.Language != identityUser.Culture && identityUser.Culture.IsNullOrWhiteSpace() == false)
            {
                anythingChanged = true;
                user.Language = identityUser.Culture;
            }
            if (user.StartMediaId != identityUser.StartMediaNode)
            {
                anythingChanged = true;
                user.StartMediaId = identityUser.StartMediaNode;
            }
            if (user.StartContentId != identityUser.StartContentNode)
            {
                anythingChanged = true;
                user.StartContentId = identityUser.StartContentNode;
            }
            if (user.AllowedSections.ContainsAll(identityUser.AllowedApplications) == false
                || identityUser.AllowedApplications.ContainsAll(user.AllowedSections) == false)
            {
                anythingChanged = true;
                foreach (var allowedSection in user.AllowedSections)
                {
                    user.RemoveAllowedSection(allowedSection);
                }
                foreach (var allowedApplication in identityUser.AllowedApplications)
                {
                    user.AddAllowedSection(allowedApplication);
                }
            }

            return anythingChanged;
        }

        /// <summary>
        /// Adds a user to a role (section)
        /// </summary>
        /// <param name="user"/><param name="roleName"/>
        /// <returns/>
        public Task AddToRoleAsync(BackOfficeIdentityUser user, string roleName)
        {
            if (user.AllowedApplications.InvariantContains(roleName)) return Task.FromResult(0);
            
            var asInt = user.Id.TryConvertTo<int>();
            if (asInt == false)
            {
                throw new InvalidOperationException("The user id must be an integer to work with the Umbraco");
            }

            var found = _userService.GetUserById(asInt.Result);

            if (found != null)
            {
                found.AddAllowedSection(roleName);
                _userService.Save(found);
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Removes the role (allowed section) for the user
        /// </summary>
        /// <param name="user"/><param name="roleName"/>
        /// <returns/>
        public Task RemoveFromRoleAsync(BackOfficeIdentityUser user, string roleName)
        {
            if (user.AllowedApplications.InvariantContains(roleName) == false) return Task.FromResult(0);

            var asInt = user.Id.TryConvertTo<int>();
            if (asInt == false)
            {
                throw new InvalidOperationException("The user id must be an integer to work with the Umbraco");
            }

            var found = _userService.GetUserById(asInt.Result);

            if (found != null)
            {
                found.RemoveAllowedSection(roleName);
                _userService.Save(found);
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns the roles for this user
        /// </summary>
        /// <param name="user"/>
        /// <returns/>
        public Task<IList<string>> GetRolesAsync(BackOfficeIdentityUser user)
        {
            return Task.FromResult((IList<string>)user.AllowedApplications.ToList());
        }

        /// <summary>
        /// Returns true if a user is in the role
        /// </summary>
        /// <param name="user"/><param name="roleName"/>
        /// <returns/>
        public Task<bool> IsInRoleAsync(BackOfficeIdentityUser user, string roleName)
        {
            return Task.FromResult(user.AllowedApplications.InvariantContains(roleName));
        }
    }
}